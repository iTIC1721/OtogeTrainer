using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Text;
using OtogeTrainer.M0.Mpv;
using OtogeTrainer.Playback.Mpv;

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
        var source = PresentationSource.FromVisual(window)!;
        var failures = 0;

        async Task StartPlayingAsync(bool guardOn = true)
        {
            window.TestPause();
            window.TestSetGuard(guardOn);
            window.TestResetLeaks();
            window.TestPlay();
            await Task.Delay(150);
        }

        // WPF 라우팅 이벤트를 직접 올린다.
        //
        // PostMessage 로 보낸 키는 창이 실제 포그라운드 포커스를 갖지 못하면 WPF 가
        // 무시해 버려서, 스크립트로 실행할 때 결과를 신뢰할 수 없다. 여기서 검증하려는
        // 것은 "가드 → 커맨드" 배선이므로 라우팅 이벤트를 직접 올리는 편이 정확하다.
        void RaiseKey(Key key, bool down)
        {
            var evt = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = evt,
            });
        }

        async Task PressAsync(Key key)
        {
            RaiseKey(key, down: true);
            await Task.Delay(60);
            RaiseKey(key, down: false);
            await Task.Delay(120);
        }

        void Check(bool ok, string label, string detail)
        {
            if (!ok) failures++;
            Emit($"{(ok ? "  ✓" : "  ✗")} {label,-44} {detail}");
        }

        Emit("════════════════════════════════════════════════════════════");
        Emit(" M0-6 입력 가드 회귀 테스트");
        Emit("════════════════════════════════════════════════════════════");
        Emit("");

        // ── 키 이벤트가 창에 도달하는가 ──────────────────────────────────────
        // 이것이 깨지면 다른 키가 "잘 막히는" 것처럼 보이지만 실제로는 차단이 아니라
        // 이벤트 소실이며, ESC 까지 못 받는다.
        Emit("FR-504  재생 중에도 키 이벤트가 창에 도달해야 한다");

        await StartPlayingAsync();
        var focused = window.TestFocusedElement;
        Check(focused is not null,
              "재생 중 키보드 포커스가 살아 있는가",
              $"FocusedElement = {focused?.GetType().Name ?? "null"}");
        Check(focused is Window,
              "포커스가 컨트롤이 아닌 창에 있는가 (§6.2-2)",
              $"{focused?.GetType().Name ?? "null"}");
        Emit("");

        // ── ESC 는 언제나 살아 있어야 한다 ───────────────────────────────────
        Emit("FR-504 / D-3  ESC → 일시정지");

        await StartPlayingAsync();
        await PressAsync(Key.Escape);
        Check(!window.TestIsPlaying, "재생 중 ESC 로 일시정지되는가",
              $"재생 {window.TestIsPlaying}");

        await PressAsync(Key.Escape);
        Check(!window.TestIsPlaying, "일시정지 중 ESC 는 아무 일도 하지 않는가",
              $"재생 {window.TestIsPlaying}");
        Emit("");

        // ── ESC 외에는 전부 막혀야 한다 ──────────────────────────────────────
        Emit("FR-501 / D-4  ESC 외 키는 재생에 영향이 없다");

        await StartPlayingAsync();
        foreach (var key in new[] { Key.Space, Key.Left, Key.Right, Key.D1, Key.F11, Key.M, Key.T })
            await PressAsync(key);
        Check(window.TestIsPlaying, "난타해도 재생이 유지되는가",
              $"재생 {window.TestIsPlaying}");

        window.TestPause();
        await Task.Delay(100);
        await PressAsync(Key.Space);
        Check(window.TestIsPlaying, "일시정지 중 Space 로 재생이 시작되는가",
              $"재생 {window.TestIsPlaying}");
        Emit("");

        // ── §6.2-6 Alt 시스템 메뉴 (Win32 경로라 PostMessage 로 검증) ────────
        Emit("§6.2-6  Alt 단독 → 시스템 메뉴");

        async Task SendAltAsync()
        {
            // 스캔코드(0x38)와 컨텍스트 비트(bit 29)를 제대로 넣어야 DefWindowProc 이
            // Alt 로 취급해 SC_KEYMENU 를 만든다. 틀리면 테스트가 조용히 통과한다.
            PostMessage(hwnd, WM_SYSKEYDOWN, VK_MENU, unchecked((int)0x20380001));
            await Task.Delay(80);
            PostMessage(hwnd, WM_SYSKEYUP, VK_MENU, unchecked((int)0xC0380001));
            await Task.Delay(350);
        }

        await StartPlayingAsync(guardOn: false);
        await SendAltAsync();
        Check(window.TestLeakCount > 0, "가드 OFF: 누출이 재현되는가 (대조군)",
              $"누출 {window.TestLeakCount}건");

        await StartPlayingAsync(guardOn: true);
        await SendAltAsync();
        Check(window.TestLeakCount == 0 && window.TestIsPlaying,
              "가드 ON: 차단되고 재생이 유지되는가",
              $"누출 {window.TestLeakCount}건, 재생 {window.TestIsPlaying}");
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
