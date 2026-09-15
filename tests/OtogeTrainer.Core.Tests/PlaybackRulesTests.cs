using OtogeTrainer.Core.Playback;
using Xunit;

namespace OtogeTrainer.Core.Tests;

public class SpeedRulesTests
{
    [Theory]
    [InlineData(0.0, 0.10)]
    [InlineData(-3, 0.10)]
    [InlineData(9, 4.00)]
    [InlineData(1.234, 1.23)]
    [InlineData(0.755, 0.76)]
    public void Normalize는_범위로_자르고_소수_둘째자리로_맞춘다(double input, double expected)
        => Assert.Equal(expected, SpeedRules.Normalize(input));

    [Fact]
    public void 미세조절을_반복해도_부동소수_오차가_쌓이지_않는다()
    {
        var speed = 0.25;
        for (var i = 0; i < 15; i++) speed = SpeedRules.StepUp(speed);

        // 0.25 + 0.05×15 = 1.00 이어야 하며 0.9999999 나 1.0000001 이면 안 된다.
        Assert.Equal(1.00, speed);

        for (var i = 0; i < 15; i++) speed = SpeedRules.StepDown(speed);
        Assert.Equal(0.25, speed);
    }

    [Fact]
    public void 미세조절은_경계에서_멈춘다()
    {
        Assert.Equal(SpeedRules.Max, SpeedRules.StepUp(SpeedRules.Max));
        Assert.Equal(SpeedRules.Min, SpeedRules.StepDown(SpeedRules.Min));
    }

    [Fact]
    public void 격자에서_벗어난_값도_미세조절하면_격자로_돌아온다()
        => Assert.Equal(0.80, SpeedRules.StepUp(0.73));

    [Theory]
    [InlineData(1.0, 1.25)]
    [InlineData(0.8, 1.0)]
    [InlineData(0.25, 0.5)]
    [InlineData(2.0, 2.0)]   // 마지막 프리셋에 머문다
    [InlineData(3.0, 2.0)]
    public void NextPreset(double current, double expected)
        => Assert.Equal(expected, SpeedRules.NextPreset(current));

    [Theory]
    [InlineData(1.0, 0.75)]
    [InlineData(0.8, 0.75)]
    [InlineData(0.25, 0.25)] // 첫 프리셋에 머문다
    [InlineData(0.1, 0.25)]
    public void PreviousPreset(double current, double expected)
        => Assert.Equal(expected, SpeedRules.PreviousPreset(current));
}

public class JumpRulesTests
{
    // D-2 확정 표 그대로
    [Theory]
    [InlineData(0.25, 1250)]
    [InlineData(0.5, 2500)]
    [InlineData(1.0, 5000)]
    [InlineData(2.0, 10000)]
    public void 점프량은_기준_곱하기_배속이다(double speed, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), JumpRules.Amount(speed));

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void 고정_모드에서는_배속과_무관하다(double speed)
        => Assert.Equal(TimeSpan.FromSeconds(5), JumpRules.Amount(speed, scaleWithSpeed: false));

    [Fact]
    public void 범위를_벗어난_배속은_정규화된_값으로_계산한다()
        => Assert.Equal(TimeSpan.FromSeconds(20), JumpRules.Amount(99));
}

public class OffsetRulesTests
{
    [Theory]
    [InlineData(-5000, -1000)]
    [InlineData(5000, 1000)]
    [InlineData(33, 33)]
    public void Clamp(int input, int expected) => Assert.Equal(expected, OffsetRules.Clamp(input));

    [Fact]
    public void 프레임_환산()
    {
        Assert.Equal(2.0, OffsetRules.ToFrames(33, 60), precision: 1);
        Assert.Equal(0, OffsetRules.ToFrames(33, 0));
    }
}

public class BookmarkSlotsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void 슬롯은_1부터_5까지(int slot, bool valid) => Assert.Equal(valid, BookmarkSlots.IsValid(slot));

    [Fact]
    public void 잘못된_슬롯은_예외()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BookmarkSlots.EnsureValid(6));
}

public class TimecodeTests
{
    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(8133, "00:00:08.133")]
    [InlineData(3_723_456, "01:02:03.456")]
    [InlineData(-10, "00:00:00.000")]
    public void Format(long ms, string expected)
        => Assert.Equal(expected, Timecode.Format(TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData(261_000, "4:21")]
    [InlineData(3_723_000, "1:02:03")]
    public void FormatShort(long ms, string expected)
        => Assert.Equal(expected, Timecode.FormatShort(TimeSpan.FromMilliseconds(ms)));
}
