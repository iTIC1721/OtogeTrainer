using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OtogeTrainer.Input;

namespace OtogeTrainer.M0;

/// <summary>
/// M0-6 검증 하네스.
/// <para>
/// 단위 테스트는 <see cref="InputGate"/> 의 판정 로직만 검증한다. 실제 사고는 WPF의 기본 동작
/// (포커스된 버튼을 Space가 누름, Alt가 시스템 메뉴를 엶 등)에서 나므로, 여기서는 그 경로들을
/// 일부러 깔아 두고 <b>자동으로 검출</b>한다. SPEC §6.2 표가 곧 이 창의 검사 항목이다.
/// </para>
/// <para>
/// "입력 가드 사용"을 끄면 같은 조작에서 누출이 재현된다 — 각 완화책이 실제로 필요한지
/// 확인하기 위한 대조군이다.
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_INITMENU = 0x0116;
    private const int SC_KEYMENU = 0xF100;

    private static readonly TimeSpan MouseRecencyWindow = TimeSpan.FromMilliseconds(400);

    private readonly IInputGate _gate = new InputGate();
    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _elapsed = new();
    private readonly StringBuilder _log = new();
    private readonly SortedDictionary<string, int> _leaks = new(StringComparer.Ordinal);

    private TimeSpan _baseOffset = TimeSpan.Zero;
    private DateTime _lastMouseAt = DateTime.MinValue;
    private bool _updatingTimeline;
    private bool _isPlaying;
    private int _keysWhilePlaying;
    private int _swallowed;
    private int _unexpectedPauses;

    public MainWindow()
    {
        InitializeComponent();

        _clock = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _clock.Tick += (_, _) => RenderClock();

        PreviewKeyDown += OnPreviewKey;
        PreviewKeyUp += OnPreviewKey;
        PreviewMouseDown += (_, _) => _lastMouseAt = DateTime.UtcNow;
        PreviewMouseWheel += (_, _) => _lastMouseAt = DateTime.UtcNow;
        PreviewGotKeyboardFocus += OnPreviewGotKeyboardFocus;
        Deactivated += (_, _) => Pause(PauseSource.AppDeactivated);

        // 비주얼 트리가 만들어진 뒤에야 컨트롤을 훑을 수 있다.
        Loaded += (_, _) => RenderState();
        RenderClock();
        RenderLeaks();
    }

    private bool GuardEnabled => ChkGuard.IsChecked == true;
    private bool AllowWhilePlaying => ChkAllowWhilePlaying.IsChecked == true;
    private bool MouseWasRecent => DateTime.UtcNow - _lastMouseAt < MouseRecencyWindow;

    private enum PauseSource { Escape, Mouse, AppDeactivated, Unexpected }

    // ── 창 프로시저 후크: Alt / F10 이 시스템 메뉴를 여는지 감시 (§6.2-6) ──────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var isKeyMenu = msg == WM_SYSCOMMAND && ((long)wParam & 0xFFF0) == SC_KEYMENU;
        if ((msg == WM_INITMENU || isKeyMenu) && _isPlaying && !MouseWasRecent)
            RecordLeak("§6.2-6  Alt/F10 이 시스템 메뉴를 열었음");

        return IntPtr.Zero;
    }

    // ── 입력 게이트 (SPEC §6.3) ──────────────────────────────────────────────

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        var ctx = new KeyEventContext(
            Key: e.Key,
            SystemKey: e.SystemKey,
            Modifiers: Keyboard.Modifiers,
            IsPlayerScreen: true,
            IsPlaying: _isPlaying,
            IsTextInputFocused: Keyboard.FocusedElement is TextBoxBase,
            AllowShortcutsWhilePlaying: AllowWhilePlaying);

        if (_isPlaying && e.IsDown)
            _keysWhilePlaying++;

        // 가드를 끄면 게이트를 통째로 우회한다 — 누출 재현용 대조군.
        if (!GuardEnabled)
        {
            if (e.IsDown) Log(ctx, "GUARD-OFF", handled: false);
            return;
        }

        var decision = _gate.Evaluate(ctx);

        switch (decision)
        {
            case InputDecision.PassThrough:
                break;

            case InputDecision.Escape:
                e.Handled = true;                       // 일시정지 중이어도 소비한다 (D-3)
                if (e.IsDown && _isPlaying) Pause(PauseSource.Escape);
                break;

            case InputDecision.Swallow:
                e.Handled = true;
                if (e.IsDown) _swallowed++;
                break;

            case InputDecision.Dispatch:
                if (e.IsDown) DispatchShortcut(ctx);
                break;
        }

        if (e.IsDown) Log(ctx, decision.ToString(), e.Handled);
    }

    /// <summary>일시정지 상태에서만 도달한다. 실제 단축키 맵 자리.</summary>
    private void DispatchShortcut(in KeyEventContext ctx)
    {
        switch (ctx.EffectiveKey)
        {
            case Key.Space: TogglePlay(PauseSource.Mouse); break;
            case Key.Left: Nudge(TimeSpan.FromSeconds(-5)); break;
            case Key.Right: Nudge(TimeSpan.FromSeconds(5)); break;
            case Key.OemComma: Nudge(TimeSpan.FromMilliseconds(-16.683)); break;
            case Key.OemPeriod: Nudge(TimeSpan.FromMilliseconds(16.683)); break;
        }
    }

    // ── 누출 검출기 ──────────────────────────────────────────────────────────

    private void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 재생 중 키보드로 포커스가 옮겨다니면 §6.2-4 / §6.2-5 누출이다.
        if (_isPlaying && !MouseWasRecent && e.NewFocus is not Window)
            RecordLeak($"§6.2-4/5  재생 중 포커스 이동 → {Describe(e.NewFocus)}");
    }

    private void OnTimelineValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimeline) return;
        if (_isPlaying && !MouseWasRecent)
            RecordLeak("§6.2-3  재생 중 슬라이더 값이 키보드로 변경됨");
    }

    /// <summary>컨트롤 바 버튼이 키보드(Space/Enter)로 활성화됐는지 검사한다. (§6.2-2)</summary>
    private bool WasActivatedByKeyboard(string label)
    {
        if (!_isPlaying || MouseWasRecent) return false;
        RecordLeak($"§6.2-2  재생 중 버튼이 키보드로 눌림 → {label}");
        return true;
    }

    private void RecordLeak(string key)
    {
        _leaks[key] = _leaks.TryGetValue(key, out var n) ? n + 1 : 1;
        RenderLeaks();
    }

    // ── 재생 상태 ────────────────────────────────────────────────────────────

    private void TogglePlay(PauseSource source)
    {
        if (_isPlaying) Pause(source);
        else Play();
    }

    private void Play()
    {
        if (_isPlaying) return;
        _isPlaying = true;
        _keysWhilePlaying = 0;
        _swallowed = 0;
        _elapsed.Restart();
        _clock.Start();

        // 재생 시작 시 포커스를 영상 표면으로 되돌린다 (§6.2-2 완화).
        if (GuardEnabled) Keyboard.ClearFocus();

        RenderState();
    }

    private void Pause(PauseSource source)
    {
        if (!_isPlaying) return;
        _isPlaying = false;
        _baseOffset += _elapsed.Elapsed;
        _elapsed.Reset();
        _clock.Stop();

        if (source == PauseSource.Unexpected)
        {
            _unexpectedPauses++;
            RecordLeak("§6.2-1/2  재생이 의도치 않게 중단됨");
        }

        RenderState();
    }

    private void Nudge(TimeSpan delta)
    {
        _baseOffset += delta;
        if (_baseOffset < TimeSpan.Zero) _baseOffset = TimeSpan.Zero;
        RenderClock();
    }

    private TimeSpan Position => _baseOffset + _elapsed.Elapsed;

    // ── 컨트롤 바 핸들러 ─────────────────────────────────────────────────────

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (WasActivatedByKeyboard("재생/일시정지")) { TogglePlay(PauseSource.Unexpected); return; }
        TogglePlay(PauseSource.Mouse);
    }

    private void OnJumpBack(object sender, RoutedEventArgs e)
    {
        WasActivatedByKeyboard("⏮ 5s");
        Nudge(TimeSpan.FromSeconds(-5));
    }

    private void OnJumpForward(object sender, RoutedEventArgs e)
    {
        WasActivatedByKeyboard("5s ⏭");
        Nudge(TimeSpan.FromSeconds(5));
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        WasActivatedByKeyboard("◀❙ 1f");
        Pause(PauseSource.Mouse);
        Nudge(TimeSpan.FromMilliseconds(-16.683));
    }

    private void OnStepForward(object sender, RoutedEventArgs e)
    {
        WasActivatedByKeyboard("1f ❙▶");
        Pause(PauseSource.Mouse);
        Nudge(TimeSpan.FromMilliseconds(16.683));
    }

    private void OnNoop(object sender, RoutedEventArgs e)
        => WasActivatedByKeyboard(((Button)sender).Content?.ToString() ?? "?");

    private void OnResetLeaks(object sender, RoutedEventArgs e)
    {
        _leaks.Clear();
        _unexpectedPauses = 0;
        _log.Clear();
        TxtLog.Text = string.Empty;
        RenderLeaks();
    }

    // ── 가드 적용 (§6.2-2 / -4 / -5 완화책) ──────────────────────────────────

    private void OnGuardToggled(object sender, RoutedEventArgs e) => RenderState();

    /// <summary>
    /// 포커스 이동 억제는 <b>재생 중에만</b> 건다. 일시정지 상태에서는 키보드로 모든 컨트롤에
    /// 닿을 수 있어야 하기 때문(NFR-11). 재생으로 진입할 때 <see cref="Keyboard.ClearFocus"/> 로
    /// 포커스를 떼어 내는 것이 §6.2-2 의 실제 완화책이다.
    /// </summary>
    private void ApplyGuard()
    {
        if (!IsLoaded) return;

        var suppress = GuardEnabled && _isPlaying && !AllowWhilePlaying;

        KeyboardNavigation.SetTabNavigation(PlayerRoot,
            suppress ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetDirectionalNavigation(PlayerRoot,
            suppress ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);

        // 컨트롤 바 버튼과 슬라이더가 포커스를 가져가지 못하게 한다.
        // 가드 토글 자체는 언제나 조작 가능해야 하므로 CheckBox 는 제외.
        foreach (var focusable in EnumerateFocusables(PlayerRoot))
            focusable.Focusable = !suppress || focusable is CheckBox;
    }

    private static string Describe(object? element) => element switch
    {
        Button b => $"Button({b.Content})",
        Slider => "Slider(타임라인)",
        CheckBox c => $"CheckBox({c.Content})",
        FrameworkElement fe when !string.IsNullOrEmpty(fe.Name) => fe.Name,
        null => "(null)",
        _ => element.GetType().Name,
    };

    private static IEnumerable<Control> EnumerateFocusables(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button or Slider or CheckBox) yield return (Control)child;
            foreach (var nested in EnumerateFocusables(child)) yield return nested;
        }
    }

    // ── 렌더링 ───────────────────────────────────────────────────────────────

    private void RenderState()
    {
        ApplyGuard();
        BtnPlayPause.Content = _isPlaying ? "❚❚  일시정지" : "▶  재생";

        if (_isPlaying)
        {
            TxtBadge.Text = AllowWhilePlaying
                ? "▶  재생 중 · 단축키 허용됨 (FR-508)"
                : "🔒  재생 중 · 단축키 꺼짐 — ESC 로 일시정지";
            TxtBadge.Foreground = (Brush)FindResource(AllowWhilePlaying ? "Fg0" : "Warn");
            TxtHint.Text = "키보드를 두드려도 시계가 멈추지 않아야 합니다";
            VideoSurface.BorderBrush = (Brush)FindResource(AllowWhilePlaying ? "Bg0" : "Warn");
        }
        else
        {
            TxtBadge.Text = "⏸  일시정지 · 단축키 사용";
            TxtBadge.Foreground = (Brush)FindResource("Fg0");
            TxtHint.Text = "Space 재생 · ←/→ 5초 · , / . 프레임 · ESC 는 아무 동작 없음 (D-3)";
            VideoSurface.BorderBrush = Brushes.Transparent;
        }

        RenderClock();
    }

    private void RenderClock()
    {
        var pos = Position;
        var frame = (long)(pos.TotalSeconds * 59.94);
        TxtClock.Text = $"{pos:hh\\:mm\\:ss\\.fff}   f#{frame}";

        _updatingTimeline = true;
        SldTimeline.Value = Math.Clamp(pos.TotalSeconds % 1000, 0, 1000);
        _updatingTimeline = false;

        TxtIntegrity.Text = _isPlaying
            ? $"재생 중 키 입력 {_keysWhilePlaying}회 · 삼킨 키 {_swallowed}회 · 의도치 않은 중단 {_unexpectedPauses}회"
            : $"의도치 않은 중단 누계 {_unexpectedPauses}회";
    }

    private void Log(in KeyEventContext ctx, string decision, bool handled)
    {
        var key = ctx.Key == Key.System ? $"System({ctx.SystemKey})" : ctx.Key.ToString();
        var mods = ctx.Modifiers == ModifierKeys.None ? "" : $"{ctx.Modifiers}+";
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {mods}{key,-18} → {decision,-12} handled={handled}\n");

        if (_log.Length > 12_000) _log.Length = 12_000;
        TxtLog.Text = _log.ToString();
    }

    private void RenderLeaks()
    {
        if (_leaks.Count == 0)
        {
            TxtLeaks.Text = "누출 없음 ✓\n\n재생을 시작한 뒤 아래를 시도해 보세요.\n\n"
                          + "· Space / 방향키 / 숫자 난타\n"
                          + "· 버튼을 클릭한 직후 Space\n"
                          + "· 슬라이더를 만진 직후 ←\n"
                          + "· Tab 연타\n"
                          + "· Alt 단독, F10, Alt+Space\n"
                          + "· F11 / M / + / T (D-4: 차단되어야 함)\n\n"
                          + "\"입력 가드 사용\"을 끄면 같은 조작에서\n누출이 재현됩니다.";
            TxtLeaks.Foreground = (Brush)FindResource("Loop");
            return;
        }

        var sb = new StringBuilder($"누출 {_leaks.Values.Sum()}건 감지\n\n");
        foreach (var (key, count) in _leaks)
            sb.AppendLine($"[{count,3}]  {key}");

        TxtLeaks.Text = sb.ToString();
        TxtLeaks.Foreground = (Brush)FindResource("Danger");
    }
}
