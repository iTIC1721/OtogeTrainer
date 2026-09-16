using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Core.Playback;
using OtogeTrainer.App.Settings;
using OtogeTrainer.Input;

namespace OtogeTrainer.App.Player;

/// <summary>
/// 플레이어 화면. 엔진 상태를 30Hz 로 읽어 표시하고, 영상별 상태를 라이브러리에 저장한다.
/// <para>
/// 저장 시점: 북마크·오프셋은 변경 즉시(NFR-08). 위치·배속은 일시정지할 때, 화면을 떠날 때,
/// 재생 중 5초마다.
/// </para>
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

    private readonly IMediaEngine _engine;
    private readonly IVideoLibrary _library;
    private readonly AppPaths _paths;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _tick;
    private readonly PlaybackKeyMap _keyMap;

    private DateTimeOffset _lastPersist;
    private long _openGeneration;

    public PlayerViewModel(
        IMediaEngine engine, IVideoLibrary library, AppPaths paths, PlaybackKeyMap keyMap, TimeProvider? clock = null)
    {
        _engine = engine;
        _library = library;
        _paths = paths;
        _keyMap = keyMap;
        _clock = clock ?? TimeProvider.System;
        _keyMap.Changed += (_, _) => OnPropertyChanged(nameof(PlaybackKeysHint));

        for (var slot = 1; slot <= BookmarkSlots.Count; slot++)
            Slots.Add(new BookmarkSlotViewModel(slot));

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TickInterval };
        _tick.Tick += (_, _) => Refresh();
    }

    // ── 상태 ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private VideoRecord? _current;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPaused = true;
    [ObservableProperty] private string _positionText = Timecode.Format(TimeSpan.Zero);
    [ObservableProperty] private string _durationText = Timecode.Format(TimeSpan.Zero);
    [ObservableProperty] private string _frameText = "f#-";
    [ObservableProperty] private double _timelineMaximum = 1;
    [ObservableProperty] private bool _isScrubbing;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText), nameof(JumpText))]
    private double _speed = SpeedRules.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText), nameof(OffsetFramesText))]
    private int _offsetMs;

    private double _timelinePosition;

    /// <summary>타임라인 위치(초). 드래그 중에는 엔진 위치로 덮어쓰지 않는다.</summary>
    public double TimelinePosition
    {
        get => _timelinePosition;
        set
        {
            if (!SetProperty(ref _timelinePosition, value)) return;
            if (IsScrubbing) PositionText = Timecode.Format(TimeSpan.FromSeconds(value));
        }
    }

    public ObservableCollection<BookmarkSlotViewModel> Slots { get; } = [];

    public IReadOnlyList<double> SpeedPresets => SpeedRules.Presets;

    public string SpeedText => $"{Speed:0.00}x";

    /// <summary>현재 배속에서 한 번 점프할 때 실제로 넘어가는 길이. 버튼 라벨에 쓴다. (UI-BLUEPRINT §4)</summary>
    public string JumpText => $"{JumpRules.Amount(Speed).TotalSeconds:0.##}s";

    public string OffsetText => OffsetMs switch
    {
        0 => "0 ms",
        > 0 => $"+{OffsetMs} ms",
        _ => $"{OffsetMs} ms",
    };

    /// <summary>FR-406 — 프레임 수 병기.</summary>
    public string OffsetFramesText => _engine.FrameRate is { } fps && OffsetMs != 0
        ? $"≈{OffsetRules.ToFrames(OffsetMs, fps):+0.0;-0.0} f"
        : "";

    public bool HasVideo => Current is not null && !IsLoading;

    /// <summary>재생 중이라 앱 단축키가 막혀 있는가. 배지 표시용. (FR-505)</summary>
    public bool IsPlaying => HasVideo && !IsPaused;

    /// <summary>화면을 떠나 라이브러리로 돌아가길 원한다. (Alt+← / ◀ 라이브러리, FR-511)</summary>
    public event EventHandler? BackRequested;

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(IsPlaying));

    partial void OnCurrentChanged(VideoRecord? value)
    {
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(IsPlaying));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(IsPlaying));
    }

    // ── 열기 / 닫기 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 영상을 열고 마지막 상태를 복원한다. (FR-103)
    /// <para>
    /// 엔진은 일시정지 상태로 파일을 연다. 그 상태에서 배속·오프셋·위치를 먼저 적용하므로
    /// 첫 프레임이 이미 복원된 위치에서 보이고, 소리도 새지 않는다.
    /// </para>
    /// </summary>
    public async Task OpenAsync(VideoRecord record)
    {
        var generation = ++_openGeneration;

        await SaveStateAsync();

        ErrorMessage = null;
        IsLoading = true;
        Current = record;
        Title = record.Title;
        IsPaused = true;

        try
        {
            await ReloadBookmarksAsync(record.Id);

            await _engine.LoadAsync(record.Path);
            if (generation != _openGeneration) return;   // 그사이 다른 영상을 열었다

            _engine.Speed = record.LastSpeed ?? SpeedRules.Default;
            _engine.AudioDelayMs = record.EffectiveOffsetMs;

            var duration = _engine.Duration;
            var resume = TimeSpan.FromMilliseconds(record.LastPositionMs);

            // 끝에서 멈췄던 영상은 처음부터. 끝 위치로 복원하면 재생을 눌러도 아무 일도 없다.
            if (duration is { } d && resume >= d - TimeSpan.FromSeconds(1)) resume = TimeSpan.Zero;
            if (resume > TimeSpan.Zero) _engine.SeekAbsolute(resume);

            _engine.IsPaused = true;
            Speed = _engine.Speed;
            OffsetMs = _engine.AudioDelayMs;
            TimelineMaximum = Math.Max(1, duration?.TotalSeconds ?? 1);
            DurationText = Timecode.Format(duration ?? TimeSpan.Zero);
            OnPropertyChanged(nameof(OffsetFramesText));

            if (duration is { } known)
                await _library.UpdateMediaInfoAsync(record.Id, (long)known.TotalMilliseconds, _engine.FrameRate);
            await _library.MarkPlayedAsync(record.Id, _clock.GetUtcNow());

            _lastPersist = _clock.GetUtcNow();
            _tick.Start();
        }
        catch (Exception ex)
        {
            Current = null;
            ErrorMessage = ex is DllNotFoundException
                ? "재생 엔진(libmpv-2.dll)을 찾을 수 없습니다. 앱과 같은 폴더에 있어야 합니다."
                : ex.Message;
        }
        finally
        {
            if (generation == _openGeneration) IsLoading = false;
        }
    }

    /// <summary>화면을 떠난다. 일시정지하고 상태와 썸네일을 남긴다.</summary>
    public async Task CloseAsync()
    {
        if (Current is null) return;

        _engine.IsPaused = true;
        IsPaused = true;
        _tick.Stop();

        await SaveStateAsync();

        // 떠나는 순간의 프레임을 썸네일로 쓴다. 최근 목록에서 "어디까지 봤는지" 가 보인다. (FR-102)
        await _engine.SaveFrameAsync(_paths.ThumbnailFor(Current.Fingerprint));
    }

    private async Task SaveStateAsync()
    {
        if (Current is not { } video || IsLoading) return;

        var position = (long)_engine.Position.TotalMilliseconds;
        await _library.SavePlaybackStateAsync(video.Id, position, _engine.Speed);
        Current = video with { LastPositionMs = position, LastSpeed = _engine.Speed };
        _lastPersist = _clock.GetUtcNow();
    }

    private async Task ReloadBookmarksAsync(long videoId)
    {
        var marks = await _library.GetBookmarksAsync(videoId);
        foreach (var slot in Slots)
            slot.Set(marks.FirstOrDefault(m => m.Slot == slot.Slot));
    }

    // ── 주기 갱신 ────────────────────────────────────────────────────────────

    private void Refresh()
    {
        if (!HasVideo) return;

        var paused = _engine.IsPaused;
        var wasPlaying = !IsPaused;
        IsPaused = paused;

        var position = _engine.Position;
        if (!IsScrubbing)
        {
            _timelinePosition = position.TotalSeconds;
            OnPropertyChanged(nameof(TimelinePosition));
            PositionText = Timecode.Format(position);
        }

        FrameText = _engine.FrameNumber is { } f ? $"f#{f}" : "f#~";

        // 일시정지로 바뀐 순간(엔진이 끝에 도달한 경우 포함)과 재생 중 주기적으로 저장한다.
        var now = _clock.GetUtcNow();
        if ((wasPlaying && paused) || (!paused && now - _lastPersist >= PersistInterval))
            Persist(SaveStateAsync);
    }

    // ── 재생 제어 ────────────────────────────────────────────────────────────

    [RelayCommand]
    public void TogglePlay()
    {
        if (!HasVideo) return;
        _engine.IsPaused = !_engine.IsPaused;
        Refresh();
    }

    /// <summary>ESC · 창 비활성화. 이미 멈춰 있으면 아무 일도 하지 않는다. (FR-504 / D-3)</summary>
    public void Pause()
    {
        if (!HasVideo || _engine.IsPaused) return;
        _engine.IsPaused = true;
        Refresh();
    }

    [RelayCommand]
    public void StepFrame(int direction)
    {
        if (!HasVideo) return;
        _engine.StepFrame(direction);
    }

    /// <summary>5초 × 배속 점프. (FR-303 / D-2)</summary>
    [RelayCommand]
    public void Jump(int direction)
    {
        if (!HasVideo) return;
        _engine.SeekRelative(JumpRules.Amount(_engine.Speed) * Math.Sign(direction));
    }

    public void BeginScrub() => IsScrubbing = HasVideo;

    /// <summary>타임라인에서 손을 뗐다. 그때 한 번만 정확 시크한다. (FR-202)</summary>
    public void EndScrub(double seconds)
    {
        if (!IsScrubbing) return;
        IsScrubbing = false;
        if (HasVideo) _engine.SeekAbsolute(TimeSpan.FromSeconds(seconds));
    }

    // ── 배속 ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    public void SetSpeed(double speed) => ApplySpeed(speed);

    [RelayCommand]
    public void SpeedStep(int direction)
        => ApplySpeed(direction > 0 ? SpeedRules.StepUp(Speed) : SpeedRules.StepDown(Speed));

    public void SpeedPreset(int direction)
        => ApplySpeed(direction > 0 ? SpeedRules.NextPreset(Speed) : SpeedRules.PreviousPreset(Speed));

    private void ApplySpeed(double speed)
    {
        if (!HasVideo) return;
        _engine.Speed = speed;
        Speed = _engine.Speed;
        Persist(SaveStateAsync);
    }

    // ── 오프셋 ───────────────────────────────────────────────────────────────

    [RelayCommand]
    public void AdjustOffset(int deltaMs) => ApplyOffset(OffsetMs + deltaMs);

    [RelayCommand]
    public void ResetOffset() => ApplyOffset(0);

    private void ApplyOffset(int ms)
    {
        if (Current is not { } video || IsLoading) return;
        _engine.AudioDelayMs = ms;
        OffsetMs = _engine.AudioDelayMs;
        Current = video with { AvOffsetMs = OffsetMs };
        var saved = OffsetMs;
        Persist(() => _library.SaveOffsetAsync(video.Id, saved));   // 즉시 저장 (NFR-08)
    }

    // ── 북마크 ───────────────────────────────────────────────────────────────

    /// <summary>슬롯 클릭: 저장돼 있으면 점프, 비어 있으면 현재 위치를 저장한다.</summary>
    [RelayCommand]
    public void ActivateSlot(int slot)
    {
        var item = Slots[slot - 1];
        if (item.IsSet) JumpToSlot(slot);
        else SaveSlot(slot);
    }

    [RelayCommand]
    public void JumpToSlot(int slot)
    {
        if (!HasVideo || Slots[slot - 1].PositionMs is not { } ms) return;
        _engine.SeekAbsolute(TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>
    /// 현재 위치를 저장한다. 요청한 시각이 아니라 엔진이 보고하는 <b>스냅된 실제 위치</b>를
    /// 저장해야 점프했을 때 같은 프레임으로 돌아온다. (M0-FINDINGS §5)
    /// </summary>
    [RelayCommand]
    public void SaveSlot(int slot)
    {
        if (Current is not { } video || !HasVideo) return;
        var ms = (long)Math.Round(_engine.Position.TotalMilliseconds);
        Slots[slot - 1].Set(new Bookmark(slot, ms));
        Persist(() => _library.SetBookmarkAsync(video.Id, slot, ms));
    }

    [RelayCommand]
    public void ClearSlot(int slot)
    {
        if (Current is not { } video) return;
        Slots[slot - 1].Set(null);
        Persist(() => _library.ClearBookmarkAsync(video.Id, slot));
    }

    /// <summary>이전 / 다음 북마크. 슬롯 번호가 아니라 시간 순으로 이동한다.</summary>
    public void NavigateBookmark(int direction)
    {
        if (!HasVideo) return;
        var marks = Slots.Where(s => s.IsSet).Select(s => new Bookmark(s.Slot, s.PositionMs!.Value));
        var now = (long)_engine.Position.TotalMilliseconds;
        var target = direction < 0 ? BookmarkNavigation.Previous(marks, now) : BookmarkNavigation.Next(marks, now);
        if (target is not null) _engine.SeekAbsolute(TimeSpan.FromMilliseconds(target.PositionMs));
    }

    [RelayCommand]
    public void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    // ── 재생 중 동작 키 (D-8) ────────────────────────────────────────────────

    /// <summary>
    /// 재생 중에도 동작하는 키가 눌렸다. 자동 반복 입력은 연속으로 해도 되는 동작(점프 · 프레임)만 받는다 —
    /// 재생/일시정지를 누르고 있으면 초당 수십 번 토글되기 때문.
    /// </summary>
    public void ExecuteAction(PlaybackAction action, bool isRepeat)
    {
        switch (action)
        {
            case PlaybackAction.TogglePlay when !isRepeat: TogglePlay(); break;
            case PlaybackAction.JumpBack: Jump(-1); break;
            case PlaybackAction.JumpForward: Jump(+1); break;
            case PlaybackAction.FrameBack: StepFrame(-1); break;
            case PlaybackAction.FrameForward: StepFrame(+1); break;
            case PlaybackAction.PreviousBookmark when !isRepeat: NavigateBookmark(-1); break;
            case PlaybackAction.NextBookmark when !isRepeat: NavigateBookmark(+1); break;
        }
    }

    /// <summary>화면 하단 안내. 설정에서 키를 바꾸면 따라 바뀐다.</summary>
    public string PlaybackKeysHint
    {
        get
        {
            string K(PlaybackAction a) => KeyText.Of(_keyMap[a]);
            return $"재생 중:  ESC 일시정지 · {K(PlaybackAction.TogglePlay)} 재생/일시정지 · "
                 + $"{K(PlaybackAction.JumpBack)} / {K(PlaybackAction.JumpForward)} 점프 · "
                 + $"{K(PlaybackAction.FrameBack)} / {K(PlaybackAction.FrameForward)} 프레임 · "
                 + $"{K(PlaybackAction.PreviousBookmark)} / {K(PlaybackAction.NextBookmark)} 북마크";
        }
    }

    // ── 단축키 (일시정지 상태에서만 도달, UI-BLUEPRINT §9) ────────────────────

    public bool HandleShortcut(KeyEventContext ctx)
    {
        var key = ctx.EffectiveKey;
        var ctrl = ctx.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = ctx.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = ctx.Modifiers.HasFlag(ModifierKeys.Alt);

        if (alt)
        {
            if (key != Key.Left) return false;
            Back();                                               // FR-511
            return true;
        }

        if (SlotFromKey(key) is { } slot)
        {
            if (ctrl) SaveSlot(slot);
            else if (shift) ClearSlot(slot);
            else JumpToSlot(slot);
            return true;
        }

        switch (key)
        {
            case Key.Space when !ctrl && !shift: TogglePlay(); return true;
            case Key.Left when shift: StepFrame(-1); return true;
            case Key.Right when shift: StepFrame(+1); return true;
            case Key.Left: Jump(-1); return true;
            case Key.Right: Jump(+1); return true;
            case Key.OemComma: StepFrame(-1); return true;
            case Key.OemPeriod: StepFrame(+1); return true;
            case Key.Up when ctrl: SpeedPreset(+1); return true;
            case Key.Down when ctrl: SpeedPreset(-1); return true;
            case Key.Up: SpeedStep(+1); return true;
            case Key.Down: SpeedStep(-1); return true;
            case Key.OemOpenBrackets: AdjustOffset(ctrl ? -OffsetRules.FineStepMs : -OffsetRules.StepMs); return true;
            case Key.OemCloseBrackets: AdjustOffset(ctrl ? OffsetRules.FineStepMs : OffsetRules.StepMs); return true;
            case Key.D0 when ctrl && shift: ResetOffset(); return true;
            default: return false;
        }
    }

    private static int? SlotFromKey(Key key) => key switch
    {
        >= Key.D1 and <= Key.D5 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad5 => key - Key.NumPad1 + 1,
        _ => null,
    };

    // ── 보조 ─────────────────────────────────────────────────────────────────

    /// <summary>저장은 화면을 막지 않는다. 실패하면 오류 배너로만 알린다.</summary>
    private async void Persist(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"저장하지 못했습니다: {ex.Message}";
        }
    }

    // ── 자동 검증용 (--smoketest) ────────────────────────────────────────────

    internal IMediaEngine Engine => _engine;
}

public sealed partial class BookmarkSlotViewModel(int slot) : ObservableObject
{
    public int Slot { get; } = slot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSet), nameof(TimeText), nameof(ToolTipText))]
    private long? _positionMs;

    public bool IsSet => PositionMs is not null;

    public string TimeText => PositionMs is { } ms ? Timecode.Format(TimeSpan.FromMilliseconds(ms)) : "비어 있음";

    public string ToolTipText => IsSet
        ? $"북마크 {Slot} · {TimeText}\n클릭: 이동 · 우클릭: 현재 위치로 저장/비우기\n단축키: {Slot} 이동 · Ctrl+{Slot} 저장 · Shift+{Slot} 비우기"
        : $"북마크 {Slot} · 비어 있음\n클릭: 현재 위치 저장\n단축키: Ctrl+{Slot}";

    public void Set(Bookmark? bookmark) => PositionMs = bookmark?.PositionMs;
}
