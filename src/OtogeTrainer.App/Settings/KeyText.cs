using System.Windows.Input;
using OtogeTrainer.Input;

namespace OtogeTrainer.App.Settings;

/// <summary>키와 동작을 화면에 보여 줄 때의 이름.</summary>
public static class KeyText
{
    private static readonly Dictionary<Key, string> Names = BuildNames();

    private static Dictionary<Key, string> BuildNames()
    {
        // Key 열거형에는 값이 같은 이름이 여럿 있다(Return=Enter, Prior=PageUp, HangulMode=KanaMode 등).
        // switch 로 쓰면 중복 패턴으로 컴파일되지 않으므로 사전에 차례로 덮어쓴다.
        var names = new Dictionary<Key, string>();
        for (var i = 0; i <= 9; i++)
        {
            names[Key.D0 + i] = i.ToString();
            names[Key.NumPad0 + i] = $"Num {i}";
        }

        names[Key.Space] = "Space";
        names[Key.Enter] = "Enter";
        names[Key.Back] = "Backspace";
        names[Key.Tab] = "Tab";
        names[Key.CapsLock] = "Caps Lock";
        names[Key.LeftShift] = "왼쪽 Shift";
        names[Key.RightShift] = "오른쪽 Shift";
        names[Key.LeftCtrl] = "왼쪽 Ctrl";
        names[Key.RightCtrl] = "오른쪽 Ctrl";
        names[Key.LeftAlt] = "왼쪽 Alt";
        names[Key.RightAlt] = "오른쪽 Alt";
        names[Key.Apps] = "메뉴 키";
        names[Key.Left] = "←";
        names[Key.Right] = "→";
        names[Key.Up] = "↑";
        names[Key.Down] = "↓";
        names[Key.PageUp] = "Page Up";
        names[Key.PageDown] = "Page Down";
        names[Key.Home] = "Home";
        names[Key.End] = "End";
        names[Key.Insert] = "Insert";
        names[Key.Delete] = "Delete";
        names[Key.PrintScreen] = "Print Screen";
        names[Key.Scroll] = "Scroll Lock";
        names[Key.Pause] = "Pause";
        names[Key.NumLock] = "Num Lock";
        names[Key.Multiply] = "Num *";
        names[Key.Add] = "Num +";
        names[Key.Subtract] = "Num -";
        names[Key.Divide] = "Num /";
        names[Key.Decimal] = "Num .";
        names[Key.OemComma] = ",";
        names[Key.OemPeriod] = ".";
        names[Key.OemMinus] = "-";
        names[Key.OemPlus] = "=";
        names[Key.OemOpenBrackets] = "[";
        names[Key.OemCloseBrackets] = "]";
        names[Key.OemQuestion] = "/";
        names[Key.OemSemicolon] = ";";
        names[Key.OemQuotes] = "'";
        names[Key.OemTilde] = "`";
        names[Key.OemPipe] = "\\";
        names[Key.OemBackslash] = "\\";
        names[Key.HangulMode] = "한/영";
        names[Key.HanjaMode] = "한자";
        return names;
    }

    public static string Of(Key? key) => key is { } k ? Names.GetValueOrDefault(k, k.ToString()) : "—";

    public static string Label(PlaybackAction action) => action switch
    {
        PlaybackAction.TogglePlay => "재생 / 일시정지",
        PlaybackAction.JumpBack => "뒤로 점프",
        PlaybackAction.JumpForward => "앞으로 점프",
        PlaybackAction.FrameBack => "프레임 뒤로",
        PlaybackAction.FrameForward => "프레임 앞으로",
        PlaybackAction.PreviousBookmark => "이전 북마크",
        PlaybackAction.NextBookmark => "다음 북마크",
        PlaybackAction.ToggleFullscreen => "전체화면",
        _ => action.ToString(),
    };

    public static string Description(PlaybackAction action) => action switch
    {
        PlaybackAction.TogglePlay => "누르고 있어도 한 번만 바뀝니다",
        PlaybackAction.JumpBack or PlaybackAction.JumpForward => "5초 × 현재 배속",
        PlaybackAction.FrameBack or PlaybackAction.FrameForward => "누르고 있으면 계속 이동 · 누르면 일시정지",
        PlaybackAction.PreviousBookmark or PlaybackAction.NextBookmark => "슬롯 번호가 아니라 시간 순",
        PlaybackAction.ToggleFullscreen => "재생은 그대로 이어집니다",
        _ => "",
    };

    /// <summary>설정 화면과 안내 문구에서 쓰는 순서. 기본 배치 F1 → F7 순서와 같다.</summary>
    public static readonly IReadOnlyList<PlaybackAction> DisplayOrder =
    [
        PlaybackAction.JumpBack, PlaybackAction.FrameBack, PlaybackAction.FrameForward, PlaybackAction.JumpForward,
        PlaybackAction.TogglePlay, PlaybackAction.PreviousBookmark, PlaybackAction.NextBookmark,
        PlaybackAction.ToggleFullscreen,
    ];
}
