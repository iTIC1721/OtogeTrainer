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

    private readonly MpvClient _mpv = new();
    private readonly SynchronizationContext _ui;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _loading;
    private bool _disposed;

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

    public void SeekAbsolute(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        _mpv.SeekAbsoluteExact(position.TotalSeconds);
    }

    public void SeekRelative(TimeSpan delta) => _mpv.SeekRelativeExact(delta.TotalSeconds);

    public void StepFrame(int direction)
    {
        if (direction > 0) _mpv.FrameStep();
        else if (direction < 0) _mpv.FrameBackStep();
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
