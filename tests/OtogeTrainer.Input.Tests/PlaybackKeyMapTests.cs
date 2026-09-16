using System.Windows.Input;
using OtogeTrainer.Input;
using Xunit;

namespace OtogeTrainer.Input.Tests;

public class PlaybackKeyMapTests
{
    [Fact]
    public void 기본_배치는_중복이_없고_F10_F12는_쓰지_않는다()
    {
        var map = new PlaybackKeyMap();
        var keys = Enum.GetValues<PlaybackAction>().Select(a => map[a]).ToArray();

        Assert.All(keys, k => Assert.NotNull(k));
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.DoesNotContain(Key.F10, keys.Select(k => k!.Value));
        Assert.DoesNotContain(Key.F12, keys.Select(k => k!.Value));
    }

    [Fact]
    public void 전체화면은_관례대로_F11()
        => Assert.Equal(Key.F11, new PlaybackKeyMap()[PlaybackAction.ToggleFullscreen]);

    [Fact]
    public void 전체화면_항목이_없던_옛_설정_파일도_F11을_받는다()
    {
        // 전체화면이 추가되기 전에 저장된 파일에는 이 항목이 없다
        var old = new PlaybackKeyMap().ToSerializable();
        old.Remove(nameof(PlaybackAction.ToggleFullscreen));

        Assert.Equal(Key.F11, PlaybackKeyMap.FromSerializable(old)[PlaybackAction.ToggleFullscreen]);
    }

    [Fact]
    public void 옛_설정에서_F11을_다른_동작에_썼다면_전체화면은_해제된다()
    {
        var old = new Dictionary<string, string> { [nameof(PlaybackAction.TogglePlay)] = "F11" };
        var map = PlaybackKeyMap.FromSerializable(old);

        Assert.Equal(Key.F11, map[PlaybackAction.TogglePlay]);
        Assert.Null(map[PlaybackAction.ToggleFullscreen]);
    }

    [Fact]
    public void 기본_배치는_왼쪽에서_오른쪽으로_시간이_앞으로_간다()
    {
        var map = new PlaybackKeyMap();
        Assert.Equal(
            [Key.F1, Key.F2, Key.F3, Key.F4],
            new[] { PlaybackAction.JumpBack, PlaybackAction.FrameBack, PlaybackAction.FrameForward, PlaybackAction.JumpForward }
                .Select(a => map[a]!.Value));
    }

    // ── 지정 가능한 키 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(Key.Escape)]          // D-3: 일시정지 전용
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    [InlineData(Key.None)]
    [InlineData(Key.System)]          // 래퍼 — 실제 키가 아니다
    [InlineData(Key.ImeProcessed)]
    public void 지정할_수_없는_키(Key key)
    {
        Assert.False(PlaybackKeyMap.IsBindable(key));
        Assert.Throws<ArgumentException>(() => new PlaybackKeyMap().Assign(PlaybackAction.TogglePlay, key));
    }

    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.A)]
    [InlineData(Key.D1)]
    [InlineData(Key.F10)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.HangulMode)]
    [InlineData(Key.OemComma)]
    [InlineData(Key.NumPad5)]
    public void ESC_외_일반_키는_모두_지정할_수_있다(Key key)
        => Assert.True(PlaybackKeyMap.IsBindable(key));

    // ── 지정 · 맞바꾸기 ──────────────────────────────────────────────────────

    [Fact]
    public void 다른_동작이_쓰던_키를_지정하면_서로_맞바꾼다()
    {
        var map = new PlaybackKeyMap();

        var displaced = map.Assign(PlaybackAction.TogglePlay, Key.F1);   // F1 은 뒤로 점프가 쓰던 키

        Assert.Equal(PlaybackAction.JumpBack, displaced);
        Assert.Equal(Key.F1, map[PlaybackAction.TogglePlay]);
        Assert.Equal(Key.F5, map[PlaybackAction.JumpBack]);                // 재생의 원래 키를 받음
    }

    [Fact]
    public void 해제된_동작이_키를_가져가면_상대는_해제된다()
    {
        var map = new PlaybackKeyMap();
        map.Unbind(PlaybackAction.TogglePlay);

        var displaced = map.Assign(PlaybackAction.TogglePlay, Key.F1);

        Assert.Equal(PlaybackAction.JumpBack, displaced);
        Assert.Null(map[PlaybackAction.JumpBack]);
    }

    [Fact]
    public void 같은_키를_다시_지정하면_아무_일도_없다()
    {
        var map = new PlaybackKeyMap();
        var changed = 0;
        map.Changed += (_, _) => changed++;

        Assert.Null(map.Assign(PlaybackAction.TogglePlay, Key.F5));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void 기본값으로_되돌리기()
    {
        var map = new PlaybackKeyMap();
        map.Assign(PlaybackAction.TogglePlay, Key.P);
        map.Unbind(PlaybackAction.JumpBack);

        map.ResetToDefaults();

        foreach (var action in Enum.GetValues<PlaybackAction>())
            Assert.Equal(PlaybackKeyMap.Defaults[action], map[action]);
    }

    // ── 찾기 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 키로_동작을_찾는다()
    {
        var map = new PlaybackKeyMap();
        Assert.Equal(PlaybackAction.TogglePlay, map.Find(Key.F5, ModifierKeys.None));
        Assert.Null(map.Find(Key.F8, ModifierKeys.None));
    }

    [Theory]
    [InlineData(ModifierKeys.Alt)]       // Alt+F4 는 창 닫기다. 점프까지 일으키면 안 된다
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    public void 다른_수정자가_함께_눌리면_동작하지_않는다(ModifierKeys modifiers)
        => Assert.Null(new PlaybackKeyMap().Find(Key.F4, modifiers));

    [Fact]
    public void 수정자_키_자체를_지정했으면_자기_플래그는_무시한다()
    {
        var map = new PlaybackKeyMap();
        map.Assign(PlaybackAction.TogglePlay, Key.RightCtrl);

        // 오른쪽 Ctrl 을 누르면 Modifiers 에 Control 이 켜진 채로 들어온다
        Assert.Equal(PlaybackAction.TogglePlay, map.Find(Key.RightCtrl, ModifierKeys.Control));
        Assert.Null(map.Find(Key.RightCtrl, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void 해제한_동작은_찾아지지_않는다()
    {
        var map = new PlaybackKeyMap();
        map.Unbind(PlaybackAction.TogglePlay);
        Assert.Null(map.Find(Key.F5, ModifierKeys.None));
    }

    // ── 저장 형식 ────────────────────────────────────────────────────────────

    [Fact]
    public void 저장하고_읽으면_같다()
    {
        var map = new PlaybackKeyMap();
        map.Assign(PlaybackAction.TogglePlay, Key.P);
        map.Unbind(PlaybackAction.NextBookmark);

        var restored = PlaybackKeyMap.FromSerializable(map.ToSerializable());

        foreach (var action in Enum.GetValues<PlaybackAction>())
            Assert.Equal(map[action], restored[action]);
    }

    [Fact]
    public void 저장_형식은_사람이_읽을_수_있는_이름이다()
    {
        var saved = new PlaybackKeyMap().ToSerializable();
        Assert.Equal("F5", saved["TogglePlay"]);
    }

    [Fact]
    public void 잘못된_값은_버리고_기본값을_쓴다()
    {
        var map = PlaybackKeyMap.FromSerializable(new Dictionary<string, string>
        {
            ["TogglePlay"] = "Escape",        // 지정 불가
            ["JumpBack"] = "없는키",
            ["FrameBack"] = "12345",          // 숫자로는 파싱되지만 정의된 키가 아님
            ["모르는동작"] = "F9",
        });

        Assert.Equal(Key.F5, map[PlaybackAction.TogglePlay]);
        Assert.Equal(Key.F1, map[PlaybackAction.JumpBack]);
        Assert.Equal(Key.F2, map[PlaybackAction.FrameBack]);
    }

    [Fact]
    public void 명시적으로_지정한_키와_기본값이_겹치면_기본값_쪽을_해제한다()
    {
        // 파일에 "재생 = F1" 만 있다. 뒤로 점프의 기본값도 F1 이라 겹친다.
        var map = PlaybackKeyMap.FromSerializable(new Dictionary<string, string> { ["TogglePlay"] = "F1" });

        Assert.Equal(Key.F1, map[PlaybackAction.TogglePlay]);
        Assert.Null(map[PlaybackAction.JumpBack]);
        Assert.Null(map.Find(Key.F5, ModifierKeys.None));
    }

    [Fact]
    public void 파일에서_두_동작에_같은_키를_적었으면_하나만_인정한다()
    {
        var map = PlaybackKeyMap.FromSerializable(new Dictionary<string, string>
        {
            ["TogglePlay"] = "P",
            ["JumpBack"] = "P",
        });

        Assert.Single(Enum.GetValues<PlaybackAction>().Where(a => map[a] == Key.P));
    }
}
