using System.Text;
using OtogeTrainer.Playback.Mpv;

namespace OtogeTrainer.M0.Mpv;

public sealed record VerifyReport(string Title, string Body, int Failures)
{
    public bool Passed => Failures == 0;
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")}  {Title}\n{Body}";
}

/// <summary>
/// M0-2 ~ M0-5 자동 검사.
/// <para>
/// GUI 버튼과 <c>--selftest</c> 헤드리스 모드가 같은 코드를 쓴다. 눈으로 보는 확인은
/// 누적 오차를 못 잡기 때문에, 판정이 필요한 항목은 전부 여기로 모은다.
/// </para>
/// </summary>
public static class PlaybackVerifier
{
    /// <summary>파일이 실제로 열려 메타데이터를 읽을 수 있을 때까지 기다린다.</summary>
    public static async Task<bool> WaitForLoadAsync(MpvClient mpv, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20; i++)
        {
            await Task.Delay(20);
            if (mpv.Duration is > 0) return true;
        }
        return false;
    }

    /// <summary>프레임 스텝은 비동기다. 프레임 번호가 바뀔 때까지 폴링한다.</summary>
    private static async Task<long> WaitFrameChangeAsync(MpvClient mpv, long previous)
    {
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(10);
            var now = mpv.EstimatedFrameNumber;
            if (now is not null && now.Value != previous) return now.Value;
        }
        return mpv.EstimatedFrameNumber ?? previous;
    }

    private static Task SettleAsync() => Task.Delay(250);

    // ── M0-2 프레임 스텝 ─────────────────────────────────────────────────────

    public static async Task<VerifyReport> VerifyFrameStepAsync(MpvClient mpv, int steps = 10)
    {
        var log = new StringBuilder();
        var failures = 0;

        mpv.Paused = true;
        mpv.SeekAbsoluteExact(10.0);
        await SettleAsync();

        var start = mpv.EstimatedFrameNumber;
        if (start is null)
        {
            return new VerifyReport("M0-2 프레임 스텝",
                "estimated-frame-number 를 읽을 수 없습니다 — VFR 이거나 미지원 형식", 1);
        }

        log.AppendLine($"시작 프레임  f#{start}");
        log.AppendLine();
        log.AppendLine($"정방향 {steps}회");

        var prev = start.Value;
        for (var i = 0; i < steps; i++)
        {
            mpv.FrameStep();
            var now = await WaitFrameChangeAsync(mpv, prev);
            var delta = now - prev;
            if (delta != 1) { failures++; log.AppendLine($"  {i + 1,2}: Δ{delta,+3}  ← 기대 +1"); }
            prev = now;
        }
        log.AppendLine($"  → f#{prev}  (Δ{prev - start.Value:+#;-#;0})");
        log.AppendLine();
        log.AppendLine($"역방향 {steps}회");

        for (var i = 0; i < steps; i++)
        {
            mpv.FrameBackStep();
            var now = await WaitFrameChangeAsync(mpv, prev);
            var delta = now - prev;
            if (delta != -1) { failures++; log.AppendLine($"  {i + 1,2}: Δ{delta,+3}  ← 기대 -1"); }
            prev = now;
        }
        log.AppendLine($"  → f#{prev}  (시작 대비 Δ{prev - start.Value:+#;-#;0})");
        log.AppendLine();

        var returned = prev == start.Value;
        if (!returned) failures++;
        log.AppendLine(returned
            ? "왕복 후 시작 프레임으로 정확히 복귀 ✓"
            : $"왕복 후 복귀 실패 — 누적 오차 {prev - start.Value} 프레임 ✗");

        return new VerifyReport("M0-2 프레임 스텝 정확도 ±1", log.ToString(), failures);
    }

    // ── M0-5 정확 시크 ───────────────────────────────────────────────────────

    public static async Task<VerifyReport> VerifySeekAsync(MpvClient mpv, IList<double> latencies)
    {
        var fps = mpv.ContainerFps ?? 60.0;
        var frameDuration = 1.0 / fps;
        double[] targets = [3.5, 17.25, 42.0, 8.125, 61.75];

        var log = new StringBuilder();
        log.AppendLine($"fps {fps:0.###}  (1프레임 = {frameDuration * 1000:0.##}ms)");
        log.AppendLine();

        var failures = 0;
        mpv.Paused = true;
        latencies.Clear();

        foreach (var target in targets)
        {
            mpv.SeekAbsoluteExact(target);
            await SettleAsync();

            var actual = mpv.PlaybackTime ?? double.NaN;
            var errorMs = (actual - target) * 1000.0;
            var ok = Math.Abs(actual - target) <= frameDuration;
            if (!ok) failures++;

            log.AppendLine($"  {target,7:0.###}s → {actual,9:0.####}s   "
                         + $"오차 {errorMs,7:+0.0;-0.0;0.0}ms  {(ok ? "✓" : "✗")}");
        }

        log.AppendLine();
        if (latencies.Count > 0)
        {
            var max = latencies.Max();
            log.AppendLine($"시크 왕복 지연  평균 {latencies.Average():0.#}ms  최대 {max:0.#}ms");
            log.AppendLine($"NFR-03 기준 150ms  {(max <= 150 ? "충족 ✓" : "미달 ✗")}");
            if (max > 150) failures++;
        }

        return new VerifyReport("M0-5 정확 시크 오차 1프레임 이내", log.ToString(), failures);
    }

    // ── M0-3 배속 + 피치 보정 ────────────────────────────────────────────────

    public static async Task<VerifyReport> VerifySpeedAsync(MpvClient mpv)
    {
        var log = new StringBuilder();
        var failures = 0;

        double[] speeds = [0.25, 0.5, 0.75, 1.0, 1.5, 2.0];
        foreach (var s in speeds)
        {
            mpv.Speed = s;
            await Task.Delay(60);
            var actual = mpv.Speed;
            var ok = Math.Abs(actual - s) < 0.001;
            if (!ok) failures++;
            log.AppendLine($"  speed {s,5:0.00} → {actual,5:0.00}  {(ok ? "✓" : "✗")}");
        }

        mpv.Speed = 1.0;

        mpv.PitchCorrection = true;
        await Task.Delay(60);
        var pitchOn = mpv.PitchCorrection;
        if (!pitchOn) failures++;
        log.AppendLine();
        log.AppendLine($"  audio-pitch-correction ON  → {pitchOn}  {(pitchOn ? "✓" : "✗")}");

        mpv.PitchCorrection = false;
        await Task.Delay(60);
        var pitchOff = !mpv.PitchCorrection;
        if (!pitchOff) failures++;
        log.AppendLine($"  audio-pitch-correction OFF → {!pitchOff}  {(pitchOff ? "✓" : "✗")}");
        mpv.PitchCorrection = true;

        return new VerifyReport("M0-3 배속 및 피치 보정", log.ToString(), failures);
    }

    // ── M0-4 A/V 오프셋 ──────────────────────────────────────────────────────

    public static async Task<VerifyReport> VerifyAudioDelayAsync(MpvClient mpv)
    {
        var log = new StringBuilder();
        var failures = 0;

        // FR-401 범위 경계와 5ms 단위 (FR-402: 양수 = 소리를 늦춤)
        double[] offsetsMs = [-1000, -333, -33, 0, 5, 33, 500, 1000];
        foreach (var ms in offsetsMs)
        {
            mpv.AudioDelay = ms / 1000.0;
            await Task.Delay(40);
            var actualMs = mpv.AudioDelay * 1000.0;
            var ok = Math.Abs(actualMs - ms) < 1.0;
            if (!ok) failures++;
            log.AppendLine($"  {ms,7:+0;-0;0} ms → {actualMs,8:+0.0;-0.0;0.0} ms  {(ok ? "✓" : "✗")}");
        }

        mpv.AudioDelay = 0;
        log.AppendLine();
        log.AppendLine("재생 중 변경이 끊김 없이 반영되는지는 귀로 확인해야 한다");
        log.AppendLine("(avsync_1s_flash_click.mp4 로 플래시와 클릭의 일치 여부 확인)");

        return new VerifyReport("M0-4 A/V 오프셋 실시간 설정", log.ToString(), failures);
    }

    // ── M0-8 VFR 판별 ────────────────────────────────────────────────────────

    public static VerifyReport InspectFrameRate(MpvClient mpv)
    {
        var container = mpv.ContainerFps;
        var vf = mpv.EstimatedVfFps;

        var log = new StringBuilder();
        log.AppendLine($"  container-fps     {container?.ToString("0.####") ?? "-"}");
        log.AppendLine($"  estimated-vf-fps  {vf?.ToString("0.####") ?? "-"}");
        log.AppendLine($"  코덱              {mpv.VideoCodec ?? "-"}");
        log.AppendLine($"  hwdec             {mpv.HwdecCurrent ?? "-"}");
        log.AppendLine($"  프레임 수         {mpv.EstimatedFrameCount?.ToString() ?? "-"}");
        log.AppendLine();

        var suspectVfr = container is > 0 && vf is > 0 && Math.Abs(container.Value - vf.Value) > 1.0;
        log.AppendLine(suspectVfr
            ? "⚠ container-fps 와 실측 fps 가 벌어짐 — VFR 의심 (SPEC R1)"
            : "고정 프레임레이트로 보임");

        // 판정이 아니라 관찰이므로 실패로 세지 않는다.
        return new VerifyReport("M0-8 프레임레이트 관찰", log.ToString(), 0);
    }
}
