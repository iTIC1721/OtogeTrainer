using System.Windows.Input;

namespace OtogeTrainer.Input;

/// <summary>키 입력을 어떻게 처리할지에 대한 판정 결과. (SPEC §6.3)</summary>
public enum InputDecision
{
    /// <summary>단축키 맵으로 라우팅한다. (일시정지 상태)</summary>
    Dispatch,

    /// <summary>소비하고 아무 동작도 하지 않는다. (재생 중 — FR-501)</summary>
    Swallow,

    /// <summary>ESC. 재생 중이면 일시정지, 일시정지 중이면 무동작. (FR-504 / D-3)</summary>
    Escape,

    /// <summary>WPF 기본 처리에 맡긴다. (텍스트 입력 포커스 등 — FR-509)</summary>
    PassThrough,

    /// <summary>재생 중 동작 키. 재생 · 일시정지 어느 상태에서든 동작한다. (D-8)</summary>
    PlaybackAction,
}

/// <summary>판정에 필요한 입력 상태 묶음. WPF 이벤트에서 채워 넣는다.</summary>
/// <param name="Key">
/// <see cref="KeyEventArgs.Key"/>. Alt/F10 계열은 <see cref="System.Windows.Input.Key.System"/>으로 들어온다.
/// </param>
/// <param name="SystemKey">
/// <see cref="KeyEventArgs.SystemKey"/>. <paramref name="Key"/>가 System일 때 실제 키가 여기 담긴다. (SPEC §6.2-6)
/// </param>
/// <param name="Modifiers">현재 눌린 수정자 키.</param>
/// <param name="IsPlayerScreen">플레이어 화면인가. 라이브러리 화면에서는 차단하지 않는다.</param>
/// <param name="IsPlaying">재생 중인가. 이 값 하나가 차단 여부를 가른다. (SPEC §6.4)</param>
/// <param name="IsTextInputFocused">텍스트 입력 컨트롤이 포커스를 가졌는가. 최우선 예외. (FR-509)</param>
/// <param name="AllowShortcutsWhilePlaying">설정 "재생 중에도 단축키 허용". (FR-508, 기본 false)</param>
/// <param name="IsPlaybackActionKey">
/// 재생 중 동작 키(<see cref="PlaybackKeyMap"/>)로 지정된 키인가. 게이트는 키 배치를 모른다 — 호출 측이 찾아서 넘긴다. (D-8)
/// </param>
/// <param name="IsRepeat">키를 누르고 있어 자동 반복된 입력인가.</param>
public readonly record struct KeyEventContext(
    Key Key,
    Key SystemKey,
    ModifierKeys Modifiers,
    bool IsPlayerScreen,
    bool IsPlaying,
    bool IsTextInputFocused,
    bool AllowShortcutsWhilePlaying,
    bool IsPlaybackActionKey = false,
    bool IsRepeat = false)
{
    /// <summary>Alt/F10 래핑을 벗겨낸 실제 키. (SPEC §6.2-6)</summary>
    public Key EffectiveKey => Key == Key.System ? SystemKey : Key;
}

/// <summary>
/// 재생 중 앱 단축키를 차단하는 게이트.
/// <para>
/// 시스템 전역 후킹(<c>SetWindowsHookEx</c>)은 사용하지 않는다 — 이 앱 창이 포커스를 가진 동안,
/// 앱 안에서만 작용한다. (SPEC §6.1)
/// </para>
/// </summary>
public interface IInputGate
{
    /// <summary>창 수준 PreviewKeyDown / PreviewKeyUp에서 키마다 한 번 호출한다.</summary>
    InputDecision Evaluate(in KeyEventContext ctx);
}

/// <summary>
/// <see cref="IInputGate"/>의 기본 구현. I/O도 로깅도 하지 않는 순수 함수다 —
/// 재생 중 난타에도 부하가 없어야 하기 때문. (SPEC §9.3, NFR-05)
/// </summary>
public sealed class InputGate : IInputGate
{
    /// <summary>
    /// 판정 순서는 SPEC §6.3 그대로다. 순서가 곧 우선순위이므로 바꾸지 말 것.
    /// </summary>
    public InputDecision Evaluate(in KeyEventContext ctx)
    {
        // 1. 텍스트 입력이 최우선. 북마크 라벨에 스페이스를 못 치면 안 된다. (FR-509)
        if (ctx.IsTextInputFocused)
            return InputDecision.PassThrough;

        // 2. 플레이어 화면이 아니면 차단 대상이 아니다.
        if (!ctx.IsPlayerScreen)
            return InputDecision.Dispatch;

        // 3. ESC는 어떤 상태에서도 차단하지 않는다. (SPEC §6.5-2)
        //    수정자가 붙은 조합(Ctrl+Esc 등)은 OS가 가져가므로 여기까지 오지 않지만,
        //    혹시 오더라도 ESC 취급하지 않는다.
        if (ctx.EffectiveKey == Key.Escape && ctx.Modifiers == ModifierKeys.None)
            return InputDecision.Escape;

        // 4. 재생 중 동작 키는 재생 중에도 동작한다. 일시정지 중에도 다른 단축키보다 우선한다 —
        //    사용자가 Space 같은 키를 지정했을 때 상태에 따라 뜻이 바뀌면 헷갈리기 때문. (D-8)
        if (ctx.IsPlaybackActionKey)
            return InputDecision.PlaybackAction;

        // 5. 재생 중이면 그 밖의 키는 전부 삼킨다. 전체화면·음소거·볼륨도 예외가 아니다. (D-4)
        if (ctx.IsPlaying && !ctx.AllowShortcutsWhilePlaying)
            return InputDecision.Swallow;

        // 6. 일시정지 상태 — 단축키 정상 동작.
        return InputDecision.Dispatch;
    }
}
