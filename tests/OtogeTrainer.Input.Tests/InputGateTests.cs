using System.Windows.Input;
using OtogeTrainer.Input;
using Xunit;

namespace OtogeTrainer.Input.Tests;

/// <summary>
/// 입력 게이트 판정 로직 검증. (SPEC §6.3 / ROADMAP §3.4)
/// 순수 함수이므로 (키 × 재생상태 × 텍스트포커스 × 설정) 조합을 전수로 돌린다.
/// </summary>
public class InputGateTests
{
    private static readonly InputGate Gate = new();

    private static KeyEventContext Ctx(
        Key key,
        bool isPlaying,
        ModifierKeys modifiers = ModifierKeys.None,
        Key systemKey = Key.None,
        bool isPlayerScreen = true,
        bool isTextInputFocused = false,
        bool allowShortcutsWhilePlaying = false,
        bool isPlaybackActionKey = false)
        => new(key, systemKey, modifiers, isPlayerScreen, isPlaying, isTextInputFocused, allowShortcutsWhilePlaying,
               isPlaybackActionKey);

    /// <summary>UI-BLUEPRINT §9 의 단축키 전체.</summary>
    public static TheoryData<Key> ShortcutKeys() =>
    [
        Key.Space, Key.Left, Key.Right, Key.Up, Key.Down,
        Key.OemComma, Key.OemPeriod,
        Key.D1, Key.D2, Key.D3, Key.D4, Key.D5,
        Key.OemOpenBrackets, Key.OemCloseBrackets,
        Key.A, Key.B, Key.L, Key.F, Key.M, Key.S, Key.G, Key.T,
        Key.F11, Key.Add, Key.Subtract, Key.OemPlus, Key.OemMinus,
        Key.Enter, Key.Tab, Key.Back, Key.Home, Key.End, Key.PageUp, Key.PageDown,
    ];

    // ── FR-501 / D-4: 재생 중에는 ESC 외 전부 차단, 예외 키 없음 ──────────────

    [Theory]
    [MemberData(nameof(ShortcutKeys))]
    public void 재생_중에는_단축키를_삼킨다(Key key)
        => Assert.Equal(InputDecision.Swallow, Gate.Evaluate(Ctx(key, isPlaying: true)));

    [Theory]
    // D-4 에서 "예외로 둘 수도 있었던" 키들. 예외 없음으로 확정했으므로 전부 차단된다.
    [InlineData(Key.F11)]   // 전체화면
    [InlineData(Key.F)]     // 전체화면
    [InlineData(Key.M)]     // 음소거
    [InlineData(Key.Add)]   // 볼륨 +
    [InlineData(Key.T)]     // 항상 위
    public void D4_예외로_거론됐던_키도_재생_중_차단된다(Key key)
        => Assert.Equal(InputDecision.Swallow, Gate.Evaluate(Ctx(key, isPlaying: true)));

    [Theory]
    [MemberData(nameof(ShortcutKeys))]
    public void 일시정지_중에는_단축키가_동작한다(Key key)
        => Assert.Equal(InputDecision.Dispatch, Gate.Evaluate(Ctx(key, isPlaying: false)));

    // ── FR-504 / D-3: ESC ────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ESC는_재생_여부와_무관하게_Escape로_판정된다(bool isPlaying)
        => Assert.Equal(InputDecision.Escape, Gate.Evaluate(Ctx(Key.Escape, isPlaying)));

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Shift)]
    public void 수정자가_붙은_ESC는_ESC로_취급하지_않는다(ModifierKeys modifiers)
        => Assert.Equal(
            InputDecision.Swallow,
            Gate.Evaluate(Ctx(Key.Escape, isPlaying: true, modifiers: modifiers)));

    // ── SPEC §6.2-6: Alt / F10 은 Key.System 으로 들어온다 ────────────────────

    [Theory]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.F10)]
    public void Alt_F10_계열도_재생_중_차단된다(Key systemKey)
        => Assert.Equal(
            InputDecision.Swallow,
            Gate.Evaluate(Ctx(Key.System, isPlaying: true, modifiers: ModifierKeys.Alt, systemKey: systemKey)));

    [Fact]
    public void EffectiveKey는_System_래핑을_벗겨낸다()
    {
        Assert.Equal(Key.F10, Ctx(Key.System, true, systemKey: Key.F10).EffectiveKey);
        Assert.Equal(Key.Space, Ctx(Key.Space, true).EffectiveKey);
    }

    [Fact]
    public void System으로_감싸인_ESC도_ESC로_인식한다()
        => Assert.Equal(
            InputDecision.Escape,
            Gate.Evaluate(Ctx(Key.System, isPlaying: true, systemKey: Key.Escape)));

    // ── FR-509: 텍스트 입력이 최우선 ─────────────────────────────────────────

    [Theory]
    [InlineData(Key.Space, true)]
    [InlineData(Key.Left, true)]
    [InlineData(Key.D1, true)]
    [InlineData(Key.Space, false)]
    [InlineData(Key.Escape, true)]
    public void 텍스트_입력_중에는_무조건_통과시킨다(Key key, bool isPlaying)
        => Assert.Equal(
            InputDecision.PassThrough,
            Gate.Evaluate(Ctx(key, isPlaying, isTextInputFocused: true)));

    // ── FR-508: 설정으로 차단 해제 ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ShortcutKeys))]
    public void 재생_중_단축키_허용_설정이_켜지면_통과한다(Key key)
        => Assert.Equal(
            InputDecision.Dispatch,
            Gate.Evaluate(Ctx(key, isPlaying: true, allowShortcutsWhilePlaying: true)));

    // ── 플레이어 화면이 아닐 때 ──────────────────────────────────────────────

    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.D1)]
    public void 라이브러리_화면에서는_차단하지_않는다(Key key)
        => Assert.Equal(
            InputDecision.Dispatch,
            Gate.Evaluate(Ctx(key, isPlaying: true, isPlayerScreen: false)));

    // ── 전수 조합 불변식 ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllKeys() =>
        Enum.GetValues<Key>().Distinct().Select(k => new object[] { k });

    /// <summary>
    /// Key enum 전체 × 상태 조합을 돌면서 SPEC §6.3 의 판정 순서가 지켜지는지 확인한다.
    /// 새 키가 추가돼도 규칙이 깨지지 않음을 보장한다.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllKeys))]
    public void 모든_키에_대해_판정_순서가_지켜진다(Key key)
    {
        foreach (var playerScreen in new[] { true, false })
        foreach (var playing in new[] { true, false })
        foreach (var textFocus in new[] { true, false })
        foreach (var allow in new[] { true, false })
        foreach (var actionKey in new[] { true, false })
        {
            var ctx = Ctx(key, playing,
                isPlayerScreen: playerScreen,
                isTextInputFocused: textFocus,
                allowShortcutsWhilePlaying: allow,
                isPlaybackActionKey: actionKey);

            var actual = Gate.Evaluate(ctx);

            var expected =
                textFocus ? InputDecision.PassThrough
                : !playerScreen ? InputDecision.Dispatch
                : ctx.EffectiveKey == Key.Escape ? InputDecision.Escape
                : actionKey ? InputDecision.PlaybackAction
                : playing && !allow ? InputDecision.Swallow
                : InputDecision.Dispatch;

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 재생 중 삼켜지지 않는 키는 ESC 와 재생 중 동작 키뿐이어야 한다. (D-4 · D-8)
    /// 키 배치는 실제 기본 배치를 쓴다.
    /// </summary>
    [Fact]
    public void 재생_중_통과하는_키는_ESC와_재생중_동작키_뿐이다()
    {
        var map = new PlaybackKeyMap();
        var passed = Enum.GetValues<Key>()
            .Distinct()
            .Where(k => Gate.Evaluate(Ctx(k, isPlaying: true,
                isPlaybackActionKey: map.Find(k, ModifierKeys.None) is not null)) != InputDecision.Swallow)
            .OrderBy(k => k)
            .ToArray();

        Assert.Equal(
            new[] { Key.Escape, Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7 }.OrderBy(k => k),
            passed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 재생중_동작키는_재생_여부와_무관하게_동작한다(bool isPlaying)
        => Assert.Equal(InputDecision.PlaybackAction,
            Gate.Evaluate(Ctx(Key.F5, isPlaying, isPlaybackActionKey: true)));

    [Fact]
    public void 텍스트_입력_중에는_재생중_동작키도_통과시킨다()
        => Assert.Equal(InputDecision.PassThrough,
            Gate.Evaluate(Ctx(Key.F5, isPlaying: true, isTextInputFocused: true, isPlaybackActionKey: true)));
}
