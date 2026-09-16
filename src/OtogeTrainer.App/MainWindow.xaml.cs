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

    // 전체화면 들어가기 직전의 창 모양. 나올 때 되돌린다.
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;

    // 전체화면에서 영상 위에 떠 있는 컨트롤 바. 처음 전체화면에 들어갈 때 만든다.
    private FloatingControlsWindow? _floating;
    private bool _controlsFloating;

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
        {
            ApplyGuard();
            UpdateFloatingControls();
        }

        if (e.PropertyName == nameof(ShellViewModel.IsFullscreen))
        {
            ApplyFullscreen(_shell.IsFullscreen);
            UpdateFloatingControls();
        }
    }

    /// <summary>
    /// 전체화면에서는 컨트롤을 영상 위에 반투명으로 띄우고, <b>재생 중에는 숨긴다</b>.
    /// 일시정지하면(ESC) 다시 나타난다. 창 모드에서는 원래대로 영상 아래에 붙는다.
    /// </summary>
    private void UpdateFloatingControls()
    {
        var fullscreen = _shell.IsFullscreen && _shell.IsPlayerScreen;
        if (fullscreen && !_controlsFloating)
        {
            _floating ??= CreateFloatingWindow();
            _floating.SetControls(PlayerView.DetachControls(), _shell.Player);
            _controlsFloating = true;
        }
        else if (!fullscreen && _controlsFloating)
        {
            _floating!.Hide();
            _floating.ClearControls();
            PlayerView.AttachControls();
            _controlsFloating = false;
            return;
        }

        if (_floating is null || !fullscreen) return;

        if (_shell.Player.IsPlaying)
        {
            _floating.Hide();
        }
        else
        {
            if (!_floating.IsVisible) _floating.Show();
            // 최대화 직후에는 창 크기가 아직 확정되지 않았다. 배치가 끝난 뒤 자리를 잡는다
            Dispatcher.BeginInvoke(PlaceFloatingControls, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private FloatingControlsWindow CreateFloatingWindow()
    {
        var window = new FloatingControlsWindow(this);
        window.SizeChanged += (_, _) => PlaceFloatingControls();
        SizeChanged += (_, _) => PlaceFloatingControls();
        LocationChanged += (_, _) => PlaceFloatingControls();
        return window;
    }

    private void PlaceFloatingControls()
    {
        if (_floating is not { IsVisible: true } || PresentationSource.FromVisual(Root) is not { CompositionTarget: { } target })
            return;

        var fromDevice = target.TransformFromDevice;
        var topLeft = fromDevice.Transform(Root.PointToScreen(new Point(0, 0)));
        var bottomRight = fromDevice.Transform(Root.PointToScreen(new Point(Root.ActualWidth, Root.ActualHeight)));
        _floating.PlaceAtBottom(new Rect(topLeft, bottomRight));
    }

    /// <summary>
    /// 테두리 없는 최대화로 작업 표시줄까지 덮는다. (FR-207)
    /// <para>
    /// 이미 최대화된 창에서 테두리만 없애면 작업 표시줄을 덮지 않는다 — WPF 는 최대화할 때의
    /// 창 스타일로 크기를 정하기 때문. 그래서 한 번 Normal 로 돌렸다가 다시 최대화한다.
    /// </para>
    /// </summary>
    private void ApplyFullscreen(bool on)
    {
        var isOn = WindowStyle == WindowStyle.None && WindowState == WindowState.Maximized;
        if (on == isOn) return;

        if (on)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = WindowState.Normal;          // 최대화 크기를 새 스타일로 다시 계산하게
            WindowState = _restoreState;
        }

        // 창 모양이 바뀌어도 키 입력은 계속 이 창이 받아야 한다
        Keyboard.Focus(this);
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

    internal FloatingControlsWindow? FloatingControls => _floating;
}
