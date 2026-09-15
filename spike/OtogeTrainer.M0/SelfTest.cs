using System.IO;
using System.Text;
using OtogeTrainer.M0.Mpv;

namespace OtogeTrainer.M0;

/// <summary>
/// 창 없이 M0-2 ~ M0-5, M0-8 을 돌리고 결과를 표준 출력과 파일로 남긴다.
/// <para><c>OtogeTrainer.M0.exe --selftest [영상경로]</c></para>
/// <para>
/// GUI 버튼만 있으면 결과를 사람이 눈으로 읽고 옮겨 적어야 한다. 자동화해 두면
/// 회귀가 생겼을 때 바로 드러난다.
/// </para>
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var output = new StringBuilder();
        void Emit(string line)
        {
            output.AppendLine(line);
            Console.WriteLine(line);
        }

        var clip = args.FirstOrDefault(a => !a.StartsWith("--"))
                   ?? FindAsset("frametest_60_cfr.mp4");

        Emit("════════════════════════════════════════════════════════════");
        Emit(" OtogeTrainer M0 셀프테스트 (헤드리스)");
        Emit($" 실행 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Emit("════════════════════════════════════════════════════════════");
        Emit("");

        if (clip is null || !File.Exists(clip))
        {
            Emit($"검증 클립을 찾을 수 없습니다: {clip ?? "(없음)"}");
            return 2;
        }

        Emit($"클립  {clip}");
        Emit("");

        using var mpv = new MpvClient();
        var failures = 0;

        try
        {
            var latencies = new List<double>();
            mpv.SeekCompleted += ms => { lock (latencies) latencies.Add(ms); };

            mpv.Initialize(IntPtr.Zero);   // 헤드리스
            Emit($"mpv   {mpv.MpvVersion ?? "(버전 불명)"}");
            Emit("");

            mpv.LoadFile(clip);
            if (!await PlaybackVerifier.WaitForLoadAsync(mpv))
            {
                Emit("파일 로드 실패 — duration 을 읽지 못했습니다.");
                return 2;
            }
            mpv.Paused = true;

            var reports = new List<VerifyReport>
            {
                PlaybackVerifier.InspectFrameRate(mpv),
                await PlaybackVerifier.VerifySpeedAsync(mpv),
                await PlaybackVerifier.VerifyAudioDelayAsync(mpv),
                await PlaybackVerifier.VerifyFrameStepAsync(mpv),
                await PlaybackVerifier.VerifySeekAsync(mpv, latencies),
            };

            foreach (var r in reports)
            {
                Emit($"── {(r.Passed ? "PASS" : "FAIL")}  {r.Title} " + new string('─', Math.Max(0, 40 - r.Title.Length)));
                Emit(r.Body.TrimEnd());
                Emit("");
                failures += r.Failures;
            }

            Emit("════════════════════════════════════════════════════════════");
            Emit(failures == 0 ? " 전체 통과" : $" 실패 {failures}건");
            Emit("════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            Emit($"예외: {ex.GetType().Name}: {ex.Message}");
            Emit(ex.StackTrace ?? "");
            failures++;
        }

        var logPath = Path.Combine(AppContext.BaseDirectory, "m0-selftest.log");
        await File.WriteAllTextAsync(logPath, output.ToString(), Encoding.UTF8);
        Console.WriteLine($"[로그] {logPath}");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// M0-1. 실제 창을 띄우고 HwndHost 에 mpv 를 붙인 뒤, VO 가 그린 화면을 PNG 로 남긴다.
    /// </summary>
    public static async Task<int> RunWindowTestAsync(string[] args)
    {
        var clip = args.FirstOrDefault(a => !a.StartsWith("--"))
                   ?? FindAsset("frametest_60_cfr.mp4");

        if (clip is null || !File.Exists(clip))
        {
            Console.WriteLine($"검증 클립을 찾을 수 없습니다: {clip ?? "(없음)"}");
            return 2;
        }

        var outPng = Path.Combine(AppContext.BaseDirectory, "m0-window.png");
        if (File.Exists(outPng)) File.Delete(outPng);

        var window = new PlaybackWindow();
        window.Show();

        var result = await window.RunWindowTestAsync(clip, outPng);
        window.Close();

        Console.WriteLine(result);
        await File.WriteAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "m0-windowtest.log"), result, Encoding.UTF8);

        return result.StartsWith("PASS") ? 0 : 1;
    }

    private static string? FindAsset(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "assets", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
