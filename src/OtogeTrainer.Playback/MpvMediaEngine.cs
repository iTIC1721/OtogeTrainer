using System.IO;
using OtogeTrainer.Core.Playback;
using OtogeTrainer.Playback.Mpv;

namespace OtogeTrainer.Playback;

/// <summary>
/// libmpv 기반 <see cref="IMediaEngine"/>. (SPEC §4.2 · M0 에서 검증된 옵션 그대로)
/// <para>
/// libmpv 이벤트는 전용 스레드에서 오므로 여기서 UI 스레드로 넘긴다. 이 클래스 밖에서는
/// 스레드를 신경 쓰지 않아도 된다.
/// </para>
/// </summary>
public sealed class MpvMediaEngine : IMediaEngine
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SeekSettleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(1);

    /// <summary>한꺼번에 쌓아 둘 수 있는 프레임 스텝 수. 빠른 드래그 뒤 한참 따라오는 일을 막는다.</summary>
    private const int MaxPendingSteps = 30;

    private readonly MpvClient _mpv = new();
    private readonly SynchronizationContext _ui;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _loading;
    private bool _disposed;

    // ── 프레임 스텝 큐 ──
    private int _pendingSteps;
    private bool _stepping;
    private long _restartCount;
    private long _restartCountAtLastSeek = -1;

    /// <summary>화면에 붙는 엔진. 표면 창이 만들어지는 즉시 초기화된다.</summary>
    public MpvMediaEngine(VideoSurfaceHost surface)
        : this()
    {
        if (surface.SurfaceHandle != IntPtr.Zero) Initialize(surface.SurfaceHandle);
        else surface.HandleReady += Initialize;
    }

    /// <summary>창 없는 엔진. 디코딩은 그대로 일어나므로 위치·프레임 계측이 동일하다. (테스트용)</summary>
    public static MpvMediaEngine CreateHeadless()
    {
        var engine = new MpvMediaEngine();
        engine.Initialize(IntPtr.Zero);
        return engine;
    }

    private MpvMediaEngine()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _mpv.EventReceived += id =>
        {
            if (id == MpvEventId.FileLoaded) Post(OnFileLoaded);
            else if (id == MpvEventId.PlaybackRestart) Post(() => _restartCount++);
        };
        _mpv.EndFile += error =>
        {
            if (error < 0) Post(() => FailLoading(error));
        };
    }

    private void Initialize(IntPtr hwnd)
    {
        try
        {
            _mpv.Initialize(hwnd);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public Task WhenReady => _ready.Task;

    public event EventHandler? FileLoaded;

    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await WhenReady;

        _loading?.TrySetCanceled();
        var loading = _loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 먼저 일시정지해 둬야 파일이 열리자마자 몇 프레임 흘러가지 않는다. 마지막 위치로
        // 복원하기 전에 소리가 새는 것도 막는다. (FR-103)
        _mpv.Paused = true;
        _mpv.LoadFile(path);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoadTimeout);
        await using var _ = timeout.Token.Register(() => loading.TrySetCanceled(timeout.Token));

        try
        {
            await loading.Task;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"영상을 {LoadTimeout.TotalSeconds:0}초 안에 열지 못했습니다: {path}");
        }
    }

    private void OnFileLoaded()
    {
        _loading?.TrySetResult();
        FileLoaded?.Invoke(this, EventArgs.Empty);
    }

    private void FailLoading(int error)
        => _loading?.TrySetException(new InvalidOperationException(
            $"영상을 열 수 없습니다. 지원하지 않는 형식이거나 손상된 파일일 수 있습니다. (mpv {error})"));

    public bool IsPaused
    {
        get => _mpv.Paused;
        set => _mpv.Paused = value;
    }

    public TimeSpan Position => TimeSpan.FromSeconds(_mpv.PlaybackTime ?? 0);

    public TimeSpan? Duration => _mpv.Duration is { } d ? TimeSpan.FromSeconds(d) : null;

    public double? FrameRate => _mpv.ContainerFps is > 0 and var fps ? fps : null;

    public long? FrameNumber => _mpv.EstimatedFrameNumber;

    public double Speed
    {
        get => _mpv.Speed;
        set => _mpv.Speed = SpeedRules.Normalize(value);
    }

    public bool PitchCorrection
    {
        get => _mpv.PitchCorrection;
        set => _mpv.PitchCorrection = value;
    }

    public int AudioDelayMs
    {
        get => (int)Math.Round(_mpv.AudioDelay * 1000.0);
        set => _mpv.AudioDelay = OffsetRules.Clamp(value) / 1000.0;
    }

    public int Volume
    {
        get => (int)Math.Round(_mpv.Volume);
        set => _mpv.Volume = VolumeRules.Clamp(value);
    }

    public bool IsMuted
    {
        get => _mpv.Mute;
        set => _mpv.Mute = value;
    }

    public void SeekAbsolute(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        BeginSeek();
        _mpv.SeekAbsoluteExact(position.TotalSeconds);
    }

    public void SeekRelative(TimeSpan delta)
    {
        BeginSeek();
        _mpv.SeekRelativeExact(delta.TotalSeconds);
    }

    /// <summary>
    /// 시크는 가장 최근 의도가 이긴다. 쌓여 있던 프레임 스텝은 버린다 — 스텝 세 번 누르고
    /// 곧바로 북마크로 점프했다면, 점프한 곳에서 스텝 세 번이 이어지길 원하진 않는다.
    /// </summary>
    private void BeginSeek()
    {
        _pendingSteps = 0;
        _restartCountAtLastSeek = _restartCount;
    }

    /// <summary>
    /// 정확히 한 프레임 이동. 연달아 불러도 전부 반영된다. (FR-301)
    /// <para>
    /// <b>왜 큐가 필요한가.</b> mpv 는 시크가 진행 중일 때 들어온 프레임 스텝을 버린다.
    /// 역방향 스텝은 내부적으로 시크라서, 역방향을 연달아 보내면 대부분 유실된다.
    /// </para>
    /// <code>
    ///   점프 직후 0ms 에 스텝      →  무시됨
    ///   역방향 5연속 (간격 0ms)    →  -1 만 반영
    ///   역방향 5연속 (간격 25ms)   →  -2 만 반영
    /// </code>
    /// <para>
    /// 우클릭 드래그로 프레임을 되감는 동작이 정확히 이 경우라, 이전 시크가 끝난 것을 확인한 뒤에
    /// 다음 스텝을 보낸다.
    /// </para>
    /// </summary>
    public void StepFrame(int direction)
    {
        if (direction == 0) return;
        _pendingSteps = Math.Clamp(_pendingSteps + Math.Sign(direction), -MaxPendingSteps, MaxPendingSteps);
        if (!_stepping) _ = PumpStepsAsync();
    }

    private async Task PumpStepsAsync()
    {
        _stepping = true;
        try
        {
            while (_pendingSteps != 0 && !_disposed)
            {
                await WaitUntilAsync(() => SeekSettled, SeekSettleTimeout);
                if (_pendingSteps == 0) break;   // 기다리는 사이 시크가 스텝을 취소했다

                var direction = Math.Sign(_pendingSteps);
                _pendingSteps -= direction;

                var frameBefore = _mpv.EstimatedFrameNumber;
                var timeBefore = _mpv.PlaybackTime;

                if (direction > 0)
                {
                    _mpv.FrameStep();
                }
                else
                {
                    _restartCountAtLastSeek = _restartCount;   // 역방향 스텝은 시크다
                    _mpv.FrameBackStep();
                }

                // VFR 이면 프레임 번호가 없을 수 있어 시각 변화도 함께 본다
                await WaitUntilAsync(
                    () => _mpv.EstimatedFrameNumber != frameBefore || _mpv.PlaybackTime != timeBefore,
                    StepTimeout);
            }
        }
        finally
        {
            _stepping = false;
        }
    }

    /// <summary>
    /// 마지막 시크 이후 PLAYBACK_RESTART 가 왔고, 지금 시크 중이 아닌가.
    /// <para>
    /// 둘 다 필요하다. 이벤트만 보면 합쳐지지 않은 앞선 시크의 재시작에 속을 수 있고,
    /// <c>seeking</c> 속성만 보면 명령 직후 시크가 아직 실행되기 전이라 false 로 읽힌다.
    /// </para>
    /// </summary>
    private bool SeekSettled => _restartCount > _restartCountAtLastSeek && !_mpv.Seeking;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(5);
    }

    public Task<bool> SaveFrameAsync(string path)
        => Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                // "video" = OSD 없이 원본 해상도
                _mpv.Command("screenshot-to-file", path, "video");
                return File.Exists(path);
            }
            catch (InvalidOperationException)
            {
                return false;   // 영상이 아직 없거나 오디오 전용 파일
            }
        });

    /// <summary>진단용. mpv 버전 문자열.</summary>
    public string? EngineVersion => _mpv.MpvVersion;

    private void Post(Action action) => _ui.Post(_ => { if (!_disposed) action(); }, null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loading?.TrySetCanceled();
        _mpv.Dispose();
    }
}
