using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;


namespace OtogeTrainer.Input;

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
    private readonly HashSet<Key> _handledDown = [];
    private bool _suppressNextKeyMenu;

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

    /// <summary>
    /// 일시정지 상태의 키 입력을 처리한다. 처리했으면 true 를 돌려준다.
    /// <para>
    /// <b>true 면 키를 소비한다.</b> 소비하지 않으면 포커스된 버튼이 같은 Space 를 한 번 더
    /// 받아, 단축키와 버튼 클릭이 동시에 일어난다(재생 토글이 두 번 = 아무 일도 없음).
    /// </para>
    /// </summary>
    public Func<KeyEventContext, bool>? ShortcutHandler { get; set; }

    /// <summary>재생 중 ESC. (FR-504 / D-3 — 일시정지 전용이므로 재생 중에만 의미가 있다)</summary>
    public event Action? EscapePressed;

    /// <summary>재생 중 동작 키 배치. 설정에서 바뀌면 같은 객체가 갱신되므로 다시 넣을 필요 없다. (D-8)</summary>
    public PlaybackKeyMap? KeyMap { get; set; }

    /// <summary>재생 중 동작 키가 눌렸다. 인자: 동작, 자동 반복 여부.</summary>
    public Action<PlaybackAction, bool>? PlaybackActionHandler { get; set; }

    /// <summary>가드가 꺼진 상태에서 누출이 관측됐을 때. 하네스 대조군용.</summary>
    public event Action<string>? LeakDetected;

    private void OnKey(object sender, KeyEventArgs e)
    {
        // e.IsDown 은 "지금 그 키가 물리적으로 눌려 있는가"라서, 우리가 알고 싶은
        // "이게 누름 이벤트인가 뗌 이벤트인가"와 미묘하게 다르다. 라우팅 이벤트로 판정한다.
        var isDown = e.RoutedEvent == Keyboard.PreviewKeyDownEvent;

        var effectiveKey = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,       // IME 가 켜져 있으면 문자 키가 이렇게 온다
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers;
        var action = KeyMap?.Find(effectiveKey, modifiers);

        var ctx = new KeyEventContext(
            Key: e.Key == Key.System ? Key.System : effectiveKey,
            SystemKey: e.SystemKey,
            Modifiers: modifiers,
            IsPlayerScreen: true,
            IsPlaying: _isPlaying(),
            IsTextInputFocused: Keyboard.FocusedElement is TextBoxBase,
            AllowShortcutsWhilePlaying: _allowWhilePlaying(),
            IsPlaybackActionKey: action is not null,
            IsRepeat: e.IsRepeat);

        if (!_enabled())
        {
            // 대조군: 가드를 끄면 WPF 기본 동작이 그대로 드러난다.
            if (isDown) KeyEvaluated?.Invoke(ctx, InputDecision.PassThrough);
            return;
        }

        var decision = _gate.Evaluate(ctx);

        switch (decision)
        {
            case InputDecision.PassThrough:
                break;

            case InputDecision.Escape:
                e.Handled = true;                            // 일시정지 중이어도 소비한다 (D-3)
                if (isDown && ctx.IsPlaying) EscapePressed?.Invoke();
                break;

            case InputDecision.Swallow:
                e.Handled = true;
                break;

            case InputDecision.PlaybackAction:
                e.Handled = true;                            // 누름·뗌 둘 다 소비
                if (isDown && action is { } a)
                {
                    // Alt · F10 을 지정했다면, 뗄 때 시스템 메뉴가 열리지 않게 한다. (§6.2-6)
                    if (effectiveKey is Key.LeftAlt or Key.RightAlt or Key.F10) _suppressNextKeyMenu = true;
                    PlaybackActionHandler?.Invoke(a, e.IsRepeat);
                }
                break;

            case InputDecision.Dispatch:
                // 누름에서 처리했으면 뗌도 함께 소비한다. KeyUp 에 반응하는 컨트롤이 있다.
                if (ShortcutHandler is { } handler && (isDown ? handler(ctx) : _handledDown.Remove(ctx.EffectiveKey)))
                {
                    if (isDown) _handledDown.Add(ctx.EffectiveKey);
                    e.Handled = true;
                }
                break;
        }

        if (isDown) KeyEvaluated?.Invoke(ctx, decision);
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

        if (Blocking || _suppressNextKeyMenu)
        {
            _suppressNextKeyMenu = false;
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

        // §6.2-2 의 실제 완화책: 포커스를 컨트롤에서 떼어 낸다.
        //
        // ★ Keyboard.ClearFocus() 를 쓰면 안 된다. 포커스가 null 이 되면 키 이벤트가
        //   라우팅될 대상이 없어져 Window.PreviewKeyDown 자체가 발생하지 않는다.
        //   그러면 ESC 까지 못 받아 일시정지가 불가능해진다(FR-504 위반). 다른 키가
        //   "잘 막히는" 것처럼 보이는 것도 차단이 아니라 이벤트 소실일 뿐이다.
        //
        //   창 자신에게 포커스를 주면 컨트롤에서는 떼어 내면서 키 이벤트는 계속 받는다.
        if (suppress || Keyboard.FocusedElement is null)
        {
            _window.Focusable = true;
            Keyboard.Focus(_window);
        }
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
