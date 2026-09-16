namespace OtogeTrainer.Core.Playback;

/// <summary>배속 규칙. (FR-203)</summary>
public static class SpeedRules
{
    public const double Min = 0.10;
    public const double Max = 4.00;
    public const double FineStep = 0.05;
    public const double Default = 1.0;

    public static readonly IReadOnlyList<double> Presets = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    /// <summary>
    /// 범위로 자르고 소수 둘째 자리로 맞춘다. 0.05 를 반복해서 더하면 부동소수 오차가 쌓여
    /// 0.7500000001 같은 값이 화면과 DB 에 새어 나가기 때문.
    /// </summary>
    public static double Normalize(double speed)
        => Math.Round(Math.Clamp(speed, Min, Max), 2, MidpointRounding.AwayFromZero);

    public static double StepUp(double current) => Normalize(SnapToStep(current) + FineStep);

    public static double StepDown(double current) => Normalize(SnapToStep(current) - FineStep);

    /// <summary>현재보다 큰 가장 가까운 프리셋. 없으면 가장 큰 프리셋에 머문다.</summary>
    public static double NextPreset(double current)
        => Presets.FirstOrDefault(p => p > current + 1e-6, Presets[^1]);

    /// <summary>현재보다 작은 가장 가까운 프리셋. 없으면 가장 작은 프리셋에 머문다.</summary>
    public static double PreviousPreset(double current)
        => Presets.LastOrDefault(p => p < current - 1e-6, Presets[0]);

    private static double SnapToStep(double value)
        => Math.Round(value / FineStep, MidpointRounding.AwayFromZero) * FineStep;
}

/// <summary>점프 규칙. (FR-303 / FR-304 / D-2)</summary>
public static class JumpRules
{
    public const double DefaultBaseSeconds = 5.0;

    /// <summary>
    /// 실제 점프량. D-2 에 따라 기본은 <c>기준 × 배속</c> 이다 — 느리게 볼수록 촘촘히 탐색하도록.
    /// </summary>
    public static TimeSpan Amount(double speed, double baseSeconds = DefaultBaseSeconds, bool scaleWithSpeed = true)
    {
        var seconds = scaleWithSpeed ? baseSeconds * SpeedRules.Normalize(speed) : baseSeconds;
        return TimeSpan.FromMilliseconds(Math.Round(seconds * 1000.0));
    }
}

/// <summary>A/V 오프셋 규칙. (FR-401 / FR-402)</summary>
public static class OffsetRules
{
    public const int MinMs = -1000;
    public const int MaxMs = 1000;
    public const int StepMs = 5;
    public const int FineStepMs = 1;

    public static int Clamp(int ms) => Math.Clamp(ms, MinMs, MaxMs);

    /// <summary>오프셋을 프레임 수로 환산한다. UI 병기용. (FR-406)</summary>
    public static double ToFrames(int ms, double fps) => fps > 0 ? ms * fps / 1000.0 : 0;
}

/// <summary>볼륨 규칙. (FR-206)</summary>
public static class VolumeRules
{
    public const int Min = 0;

    /// <summary>100 = 원본 음량. 증폭은 하지 않는다 — 녹화 음량 비교가 흐려진다.</summary>
    public const int Max = 100;

    public const int Default = 100;
    public const int Step = 5;

    public static int Clamp(int volume) => Math.Clamp(volume, Min, Max);
}

/// <summary>북마크 슬롯 규칙. (FR-321 / D-5)</summary>
public static class BookmarkSlots
{
    public const int Count = 5;

    public static bool IsValid(int slot) => slot is >= 1 and <= Count;

    public static void EnsureValid(int slot)
    {
        if (!IsValid(slot))
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"북마크 슬롯은 1~{Count} 이어야 합니다.");
    }
}

/// <summary>시간 표기.</summary>
public static class Timecode
{
    /// <summary><c>HH:MM:SS.mmm</c>. 음수는 0 으로 본다.</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    /// <summary>카드용 짧은 표기. 1시간 미만이면 <c>M:SS</c>.</summary>
    public static string FormatShort(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }
}

/// <summary>사이드 버튼 · 단축키로 이전/다음 북마크로 건너뛰는 규칙. (SPEC §6.6)</summary>
public static class BookmarkNavigation
{
    /// <summary>
    /// 이 거리 안쪽의 북마크는 "지금 여기" 로 본다. 북마크로 점프한 직후 이전 버튼을 누르면
    /// 같은 북마크에 머물지 않고 그 앞 북마크로 가야 하기 때문. 재생 중이면 점프 후에도
    /// 위치가 조금 흘러가므로 한 프레임보다 넉넉히 잡는다.
    /// </summary>
    public const long ProximityMs = 300;

    public static Library.Bookmark? Previous(IEnumerable<Library.Bookmark> bookmarks, long currentMs)
        => bookmarks
            .Where(b => b.PositionMs < currentMs - ProximityMs)
            .MaxBy(b => b.PositionMs);

    public static Library.Bookmark? Next(IEnumerable<Library.Bookmark> bookmarks, long currentMs)
        => bookmarks
            .Where(b => b.PositionMs > currentMs + ProximityMs)
            .MinBy(b => b.PositionMs);
}
