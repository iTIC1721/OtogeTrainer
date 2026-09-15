using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OtogeTrainer.Input;

namespace OtogeTrainer.M0.Input;

/// <summary>
/// 창 하나에 입력 게이트를 붙인다. 키를 만지는 곳은 여기 한 군데뿐이어야 한다. (SPEC R7)
/// <para>
/// <see cref="InputGate"/> 는 "무엇을 할지"만 정하는 순수 함수다. 실제로 새는 경로를 막는
/// 일(<see cref="RoutedEventArgs.Handled"/>, 포커스 억제, 시스템 메뉴 차단)은 전부 여기서 한다.
/// </para>
/// </summary>
public sealed class WindowInputGuard
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;

    private readonly Window _window;
    private readonly IInputGate _gate = new InputGate();
    private readonly Func<bool> _isPlaying;
    private readonly Func<bool> _allowWhilePlaying;
    private readonly Func<bool> _enabled;

    public WindowInputGuard(
        Window window,
        Func<bool> isPlaying,
        Func<bool>? allowWhilePlaying = null,
        Func<bool>? enabled = null)
    {
        _window = window;
        _isPlaying = isPlaying;
        _allowWhilePlaying = allowWhilePlaying ?? (() => false);
        _enabled = enabled ?? (() => true);

        window.PreviewKeyDown += OnKey;
        window.PreviewKeyUp += OnKey;
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(WndProc);
        };
    }

    /// <summary>지금 키를 삼켜야 하는 상태인가.</summary>
    public bool Blocking => _enabled() && _isPlaying() && !_allowWhilePlaying();

    /// <summary>로깅용. 판정 결과를 그대로 흘려보낸다.</summary>
    public event Action<KeyEventContext, InputDecision>? KeyEvaluated;

    /// <summary>일시정지 상태에서만 발생한다. 실제 단축키 처리는 각 창이 한다.</summary>
    public event Action<KeyEventContext>? ShortcutRequested;

    /// <summary>재생 중 ESC. (FR-504 / D-3 — 일시정지 전용이므로 재생 중에만 의미가 있다)</summary>
    public event Action? EscapePressed;

    /// <summary>가드가 꺼진 상태에서 누출이 관측됐을 때. 하네스 대조군용.</summary>
    public event Action<string>? LeakDetected;

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctx = new KeyEventContext(
            Key: e.Key,
            SystemKey: e.SystemKey,
            Modifiers: Keyboard.Modifiers,
            IsPlayerScreen: true,
            IsPlaying: _isPlaying(),
            IsTextInputFocused: Keyboard.FocusedElement is TextBoxBase,
            AllowShortcutsWhilePlaying: _allowWhilePlaying());

        if (!_enabled())
        {
            // 대조군: 가드를 끄면 WPF 기본 동작이 그대로 드러난다.
            if (e.IsDown) KeyEvaluated?.Invoke(ctx, InputDecision.PassThrough);
            return;
        }

        var decision = _gate.Evaluate(ctx);

        switch (decision)
        {
            case InputDecision.PassThrough:
                break;

            case InputDecision.Escape:
                e.Handled = true;                            // 일시정지 중이어도 소비한다 (D-3)
                if (e.IsDown && ctx.IsPlaying) EscapePressed?.Invoke();
                break;

            case InputDecision.Swallow:
                e.Handled = true;
                break;

            case InputDecision.Dispatch:
                if (e.IsDown) ShortcutRequested?.Invoke(ctx);
                break;
        }

        if (e.IsDown) KeyEvaluated?.Invoke(ctx, decision);
    }

    /// <summary>
    /// Alt / F10 단독으로 열리는 창 시스템 메뉴를 막는다.
    /// <para>
    /// <b>이것이 <c>e.Handled = true</c> 로는 막히지 않는 이유:</b> 시스템 메뉴는 라우팅 이벤트가
    /// 아니라 <c>DefWindowProc</c> 이 <c>WM_SYSCOMMAND(SC_KEYMENU)</c> 를 처리하면서 열린다.
    /// 메시지 단계에서 잘라내야 한다.
    /// </para>
    /// <para>
    /// <c>lParam</c> 이 0 이면 Alt/F10 단독이다. <c>Alt+Space</c> 처럼 문자가 붙은 조합은
    /// 윈도우 기본 동작이므로 건드리지 않는다. (FR-502)
    /// </para>
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_SYSCOMMAND || ((long)wParam & 0xFFF0) != SC_KEYMENU)
            return IntPtr.Zero;

        var altAlone = lParam == IntPtr.Zero;
        if (!altAlone) return IntPtr.Zero;      // Alt+Space 등은 통과

        if (Blocking)
        {
            handled = true;                     // DefWindowProc 로 넘기지 않는다
            return IntPtr.Zero;
        }

        if (_isPlaying())
            LeakDetected?.Invoke("§6.2-6  Alt/F10 이 시스템 메뉴를 열었음");

        return IntPtr.Zero;
    }

    /// <summary>
    /// 포커스 이동 억제는 <b>재생 중에만</b> 건다. 일시정지 상태에서 Tab 을 막으면
    /// NFR-11(모든 기능에 키보드 경로)이 깨진다.
    /// </summary>
    public void ApplyFocusSuppression(DependencyObject root)
    {
        var suppress = Blocking;

        KeyboardNavigation.SetTabNavigation(root,
            suppress ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetDirectionalNavigation(root,
            suppress ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);

        // 가드 토글 자체는 언제나 조작 가능해야 하므로 CheckBox 는 제외한다.
        foreach (var control in EnumerateControls(root))
            control.Focusable = !suppress || control is CheckBox;

        // 재생 진입 시 포커스를 컨트롤에서 떼어 내는 것이 §6.2-2 의 실제 완화책이다.
        if (suppress) Keyboard.ClearFocus();
    }

    private static IEnumerable<Control> EnumerateControls(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button or Slider or CheckBox) yield return (Control)child;
            foreach (var nested in EnumerateControls(child)) yield return nested;
        }
    }
}
