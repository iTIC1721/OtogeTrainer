using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OtogeTrainer.App.Player;
using OtogeTrainer.Input;
using OtogeTrainer.Playback;

namespace OtogeTrainer.App;

public partial class MainWindow : Window
{
    private const int WM_MOUSEWHEEL = 0x020A;

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

        // 재생 상태나 화면이 바뀌면 포커스 억제를 다시 계산한다.
        _shell.Player.PropertyChanged += OnStateChanged;
        _shell.PropertyChanged += OnStateChanged;

        // ── 영상 위 마우스 (SPEC §6.6). 재생 중에는 이것이 유일한 조작 경로다(D-4). ─
        surface.Clicked += (_, _) => _shell.Player.TogglePlay();
        surface.WheelTurned += (_, notches) => _shell.Player.JumpBy(-notches);   // 휠 위 = 뒤로
        surface.FrameDragStep += (_, direction) => _shell.Player.DragStepFrame(direction);
        surface.SideButton += (_, direction) => _shell.Player.NavigateBookmark(direction);

        // 영상을 클릭해도 키보드 포커스는 이 창에 남아야 한다. 놓치면 ESC 가 먹지 않는다.
        surface.PointerPressed += (_, _) => Keyboard.Focus(this);

        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
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

    /// <summary>
    /// 휠 메시지가 커서 아래(영상 표면)가 아니라 포커스를 가진 이 창으로 오는 경우가 있다
    /// ("비활성 창 위에서 스크롤" 설정이 꺼져 있을 때). 커서가 영상 위면 표면으로 넘긴다.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEWHEEL && _shell.IsPlayerScreen && _surface.TryHandleForwardedWheel(wParam))
            handled = true;
        return IntPtr.Zero;
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
}
