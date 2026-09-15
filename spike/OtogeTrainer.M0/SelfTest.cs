using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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

    /// <summary>
    /// Alt / F10 단독이 창 시스템 메뉴를 여는 누출(§6.2-6)에 대한 회귀 테스트.
    /// <para>
    /// 이 경로는 <c>e.Handled = true</c> 로 막히지 않는다 — 메뉴는 라우팅 이벤트가 아니라
    /// <c>DefWindowProc</c> 의 <c>WM_SYSCOMMAND(SC_KEYMENU)</c> 에서 열리기 때문.
    /// 가드를 껐을 때 누출이 재현되는지도 함께 확인해야 테스트가 실제로 그 경로를
    /// 건드리고 있음이 보장된다.
    /// </para>
    /// </summary>
    public static async Task<int> RunInputTestAsync()
    {
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;
        const int VK_MENU = 0x12;

        var output = new StringBuilder();
        void Emit(string line) { output.AppendLine(line); Console.WriteLine(line); }

        var window = new MainWindow();
        window.Show();
        window.Activate();
        await Task.Delay(400);

        var hwnd = new WindowInteropHelper(window).Handle;

        async Task<(int leaks, string detail)> PressAltAsync(bool guardOn)
        {
            window.TestPause();
            window.TestSetGuard(guardOn);
            window.TestResetLeaks();
            window.TestPlay();
            await Task.Delay(150);

            // 실제 Alt 누름/뗌 시퀀스. DefWindowProc 이 WM_SYSKEYUP(VK_MENU) 을 받으면
            // SC_KEYMENU 를 보낸다.
            PostMessage(hwnd, WM_SYSKEYDOWN, VK_MENU, 0x20380001);
            await Task.Delay(80);
            PostMessage(hwnd, WM_SYSKEYUP, VK_MENU, unchecked((int)0xC0380001));
            await Task.Delay(400);

            return (window.TestLeakCount, window.TestLeakSummary);
        }

        Emit("════════════════════════════════════════════════════════════");
        Emit(" §6.2-6 Alt 시스템 메뉴 회귀 테스트");
        Emit("════════════════════════════════════════════════════════════");
        Emit("");

        var failures = 0;

        var off = await PressAltAsync(guardOn: false);
        var offOk = off.leaks > 0;
        if (!offOk) failures++;
        Emit($"가드 OFF (대조군)  누출 {off.leaks}건  {(offOk ? "✓ 경로 재현됨" : "✗ 누출이 재현되지 않음 — 테스트가 이 경로를 못 건드림")}");
        Emit($"                   {off.detail}");
        Emit("");

        var on = await PressAltAsync(guardOn: true);
        var onOk = on.leaks == 0 && window.TestIsPlaying;
        if (!onOk) failures++;
        Emit($"가드 ON            누출 {on.leaks}건  재생 유지 {window.TestIsPlaying}  {(onOk ? "✓ 차단됨" : "✗ 여전히 새고 있음")}");
        Emit($"                   {on.detail}");
        Emit("");

        Emit("════════════════════════════════════════════════════════════");
        Emit(failures == 0 ? " 통과" : $" 실패 {failures}건");
        Emit("════════════════════════════════════════════════════════════");
        Emit("");
        Emit("※ Alt+Space 는 lParam 이 0 이 아니므로 차단 대상이 아니다 (FR-502).");
        Emit("  창 시스템 메뉴가 정상적으로 열리는지는 손으로 확인할 것.");

        window.Close();

        await File.WriteAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "m0-inputtest.log"), output.ToString(), Encoding.UTF8);

        return failures == 0 ? 0 : 1;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, int lParam);

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
