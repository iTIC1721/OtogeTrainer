using System.Windows.Input;

namespace OtogeTrainer.Input;

/// <summary>
/// 재생 중에도 동작하는 조작. ESC 를 제외하면 재생 중 앱이 반응하는 키는 이것뿐이다. (D-8)
/// <para>
/// 원래는 영상 위 마우스 제스처였으나, 마우스를 쓰는 리듬게임이 있어 영상 위 마우스 조작을
/// 없애고 키로 옮겼다.
/// </para>
/// </summary>
public enum PlaybackAction
{
    TogglePlay,
    JumpBack,
    JumpForward,
    FrameBack,
    FrameForward,
    PreviousBookmark,
    NextBookmark,
}

/// <summary>
/// <see cref="PlaybackAction"/> 마다 키 하나. 설정에서 ESC 를 제외한 아무 키로나 바꿀 수 있다. (D-8)
/// </summary>
public sealed class PlaybackKeyMap
{
    /// <summary>
    /// 기본 배치. 리듬게임이 거의 쓰지 않는 F키 중에서 고르되,
    /// F10(Alt 와 같은 시스템 메뉴 키) · F11(전체화면 예정) · F12(Windows 가 디버거용으로 예약)는 피한다.
    /// F1~F4 는 왼쪽에서 오른쪽으로 갈수록 시간이 앞으로 간다.
    /// </summary>
    public static readonly IReadOnlyDictionary<PlaybackAction, Key> Defaults = new Dictionary<PlaybackAction, Key>
    {
        [PlaybackAction.JumpBack] = Key.F1,
        [PlaybackAction.FrameBack] = Key.F2,
        [PlaybackAction.FrameForward] = Key.F3,
        [PlaybackAction.JumpForward] = Key.F4,
        [PlaybackAction.TogglePlay] = Key.F5,
        [PlaybackAction.PreviousBookmark] = Key.F6,
        [PlaybackAction.NextBookmark] = Key.F7,
    };

    private readonly Dictionary<PlaybackAction, Key?> _map = [];

    public PlaybackKeyMap() => ResetToDefaults();

    /// <summary>설정이 바뀌었다. 저장과 화면 갱신에 쓴다.</summary>
    public event EventHandler? Changed;

    /// <summary>동작에 지정된 키. 해제했으면 null.</summary>
    public Key? this[PlaybackAction action] => _map.GetValueOrDefault(action);

    /// <summary>
    /// 지정할 수 있는 키인가.
    /// <list type="bullet">
    /// <item>ESC — 일시정지 전용이라 다른 동작에 줄 수 없다 (D-3)</item>
    /// <item>Windows 키 — 시작 메뉴가 먼저 가져간다</item>
    /// <item>System · ImeProcessed 등 — 실제 키가 아니라 래퍼. 호출 측이 벗겨서 넘겨야 한다</item>
    /// </list>
    /// </summary>
    public static bool IsBindable(Key key) => key is not (
        Key.None or Key.Escape or Key.LWin or Key.RWin or
        Key.System or Key.ImeProcessed or Key.DeadCharProcessed);

    /// <summary>
    /// 키를 지정한다. 그 키를 이미 다른 동작이 쓰고 있으면 <b>서로 맞바꾼다</b> —
    /// 다른 동작이 이 동작의 원래 키를 받는다. 조용히 해제되는 동작이 없도록.
    /// </summary>
    /// <returns>맞바뀐 동작. 없으면 null.</returns>
    public PlaybackAction? Assign(PlaybackAction action, Key key)
    {
        if (!IsBindable(key))
            throw new ArgumentException($"{key} 는 지정할 수 없는 키입니다.", nameof(key));

        var previous = this[action];
        if (previous == key) return null;

        PlaybackAction? displaced = null;
        foreach (var (other, otherKey) in _map)
        {
            if (other != action && otherKey == key)
            {
                displaced = other;
                break;
            }
        }

        _map[action] = key;
        if (displaced is { } d) _map[d] = previous;

        Changed?.Invoke(this, EventArgs.Empty);
        return displaced;
    }

    public void Unbind(PlaybackAction action)
    {
        if (this[action] is null) return;
        _map[action] = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefaults()
    {
        foreach (var action in Enum.GetValues<PlaybackAction>())
            _map[action] = Defaults[action];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 눌린 키가 어느 동작인지 찾는다.
    /// <para>
    /// <b>다른 수정자 키가 함께 눌려 있으면 동작하지 않는다.</b> <c>Alt+F4</c>(창 닫기)가
    /// "앞으로 점프" 까지 일으키면 안 되기 때문. 단, 지정한 키가 수정자 자신이면(예: 오른쪽 Ctrl)
    /// 그 키가 켠 수정자 플래그는 무시한다.
    /// </para>
    /// </summary>
    public PlaybackAction? Find(Key key, ModifierKeys modifiers)
    {
        var others = modifiers & ~OwnModifier(key);
        if (others != ModifierKeys.None) return null;

        foreach (var (action, bound) in _map)
            if (bound == key) return action;
        return null;
    }

    private static ModifierKeys OwnModifier(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        _ => ModifierKeys.None,
    };

    // ── 저장 형식 ────────────────────────────────────────────────────────────

    /// <summary>JSON 저장용. 사람이 읽을 수 있게 동작과 키를 이름으로 쓴다. 해제된 동작은 빈 문자열.</summary>
    public Dictionary<string, string> ToSerializable()
        => _map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value?.ToString() ?? "");

    /// <summary>
    /// 저장된 값을 읽는다. 알 수 없는 동작·키, 지정할 수 없는 키, 중복은 버리고 기본값을 쓴다 —
    /// 설정 파일을 손으로 고치다 틀려도 앱이 키를 잃지 않게.
    /// </summary>
    public static PlaybackKeyMap FromSerializable(IReadOnlyDictionary<string, string>? values)
    {
        var map = new PlaybackKeyMap();
        if (values is null) return map;

        var used = new HashSet<Key>();
        var explicitlySet = new HashSet<PlaybackAction>();

        foreach (var action in Enum.GetValues<PlaybackAction>())
        {
            if (!values.TryGetValue(action.ToString(), out var name)) continue;

            if (name == "")
            {
                map._map[action] = null;
                explicitlySet.Add(action);
            }
            else if (Enum.TryParse<Key>(name, out var key) && Enum.IsDefined(key) && IsBindable(key) && used.Add(key))
            {
                map._map[action] = key;
                explicitlySet.Add(action);
            }
        }

        // 기본값으로 남은 동작(항목이 없거나 값이 잘못됨)이 명시적으로 지정된 키와 겹치면 해제한다.
        foreach (var action in Enum.GetValues<PlaybackAction>())
        {
            if (explicitlySet.Contains(action)) continue;
            if (map._map[action] is { } k && !used.Add(k)) map._map[action] = null;
        }

        return map;
    }
}
