using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OtogeTrainer.App.Player;
using OtogeTrainer.Input;
using OtogeTrainer.Playback;

namespace OtogeTrainer.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly VideoSurfaceHost _surface;
    private readonly WindowInputGuard _guard;
    private bool _shutdownSaved;

    public MainWindow(ShellViewModel shell, VideoSurfaceHost surface)
    {
        InitializeComponent();
        DataContext = _shell = shell;
        _surface = surface;
        PlayerView.AttachSurface(surface);

        // ── 재생 중 단축키 차단 (SPEC §6 / M0-6 에서 검증한 가드 그대로) ──────────
        _guard = new WindowInputGuard(this, isPlaying: () => _shell.IsPlaying);
        _guard.EscapePressed += () => _shell.Player.Pause();
        _guard.ShortcutHandler = _shell.HandleShortcut;
        _guard.KeyMap = _shell.KeyMap;                                          // D-8
        _guard.PlaybackActionHandler = _shell.HandlePlaybackAction;

        // 재생 상태나 화면이 바뀌면 포커스 억제를 다시 계산한다.
        _shell.Player.PropertyChanged += OnStateChanged;
        _shell.PropertyChanged += OnStateChanged;

        // ── 영상 위 마우스는 아무 동작도 하지 않는다 (D-8). ────────────────────────
        // 마우스를 쓰는 리듬게임을 따라 치다가 영상을 건드려도 재생이 흔들리면 안 된다.
        // 다만 영상을 클릭한 뒤에도 키보드 포커스는 이 창에 남아야 한다 — 놓치면 ESC 도,
        // 재생 중 동작 키도 먹지 않는다.
        surface.PointerPressed += (_, _) => Keyboard.Focus(this);

        Deactivated += (_, _) => _shell.Player.Pause();                           // FR-507
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            ApplyGuard();
            await _shell.StartAsync();
        };
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(ShellViewModel.Screen))
            ApplyGuard();
    }

    private void ApplyGuard()
    {
        if (IsLoaded) _guard.ApplyFocusSuppression(Root);
    }

    // ── 드래그 & 드롭 (FR-101) ───────────────────────────────────────────────

    private static string? FirstVideo(DragEventArgs e)
        => e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(AppPaths.IsVideoFile) : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = FirstVideo(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (FirstVideo(e) is { } path)
        {
            Activate();
            await _shell.OpenPathAsync(path);
        }
    }

    // ── 종료 ─────────────────────────────────────────────────────────────────

    /// <summary>창을 닫기 전에 위치와 썸네일을 남긴다. 저장이 끝난 뒤 다시 닫는다.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownSaved) return;
        e.Cancel = true;

        try { await _shell.ShutdownAsync(); }
        catch (IOException) { /* 종료를 막지는 않는다 */ }

        _shutdownSaved = true;

        // Closing 이벤트가 끝난 뒤에 다시 닫는다. 라이브러리 화면에서는 저장할 게 없어
        // ShutdownAsync 가 동기로 끝나므로, 여기서 곧바로 Close() 를 부르면 아직 Closing
        // 처리 도중이라 "닫는 중에는 Close 를 호출할 수 없습니다" 예외가 난다.
        _ = Dispatcher.BeginInvoke(Close);
    }

    internal bool TestShutdownSaved => _shutdownSaved;

    // ── 자동 검증용 (--smoketest) ────────────────────────────────────────────

    internal VideoSurfaceHost Surface => _surface;

    internal System.Windows.Controls.Slider Timeline => PlayerView.TimelineSlider;
}
