using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using OtogeTrainer.Core.Playback;
using OtogeTrainer.Data;

namespace OtogeTrainer.App;

/// <summary>
/// M1 종단 자동 검증. 실제 창 · 실제 엔진 · 임시 DB 로 사용자 시나리오를 처음부터 끝까지 돈다.
/// <para><c>OtogeTrainer.exe --smoketest [영상]</c></para>
/// <para>
/// 단위 테스트는 각 층을 따로 본다. 여기서는 층 사이의 배선 — "저장한 게 정말 다시 열 때
/// 복원되는가", "영상을 클릭한 뒤에도 ESC 가 먹는가" — 를 본다. M0 에서 결함은 전부 이
/// 틈에서 나왔다.
/// </para>
/// </summary>
internal static class SmokeTest
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONUP = 0x020C;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static async Task<int> RunAsync(MainWindow window, ShellViewModel shell, AppPaths paths, string[] args)
    {
        var log = new StringBuilder();
        var failures = 0;

        void Emit(string line) { log.AppendLine(line); Console.WriteLine(line); }

        void Check(bool ok, string label, string detail = "")
        {
            if (!ok) failures++;
            Emit($"  {(ok ? "✓" : "✗")} {label,-46} {detail}");
        }

        static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 4000)
        {
            for (var waited = 0; waited < timeoutMs; waited += 25)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }
            return condition();
        }

        var player = shell.Player;
        var engine = player.Engine;
        var clip = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a)) ?? FindAsset("frametest_60_cfr.mp4");

        async Task SeekAndSettle(double seconds)
        {
            engine.SeekAbsolute(TimeSpan.FromSeconds(seconds));
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - seconds) < 0.001);
            await Task.Delay(120);
        }

        Emit("════════════════════════════════════════════════════════════");
        Emit(" OtogeTrainer M1 종단 검증");
        Emit($" {DateTime.Now:yyyy-MM-dd HH:mm:ss}   데이터 {paths.DataDirectory}");
        Emit("════════════════════════════════════════════════════════════");

        if (clip is null)
        {
            Emit("검증 클립이 없습니다. bash tools/make-test-clips.sh 를 먼저 실행하세요.");
            return 2;
        }

        try
        {
            await WaitUntil(() => window.IsLoaded);

            // ── 1. 열기 ─────────────────────────────────────────────────────
            Emit("");
            Emit("1. 파일 열기 (FR-101)");
            await shell.OpenPathAsync(clip);
            await WaitUntil(() => player.HasVideo);

            Check(shell.IsPlayerScreen, "플레이어 화면으로 전환");
            Check(player.HasVideo && player.ErrorMessage is null, "영상이 열림", player.ErrorMessage ?? "");
            Check(player.IsPaused, "일시정지 상태로 시작 (FR-103)");
            Check(engine.Position == TimeSpan.Zero, "처음 여는 영상은 0초에서", Timecode.Format(engine.Position));

            // ── 2. 조작 ─────────────────────────────────────────────────────
            Emit("");
            Emit("2. 배속 · 오프셋 · 북마크 (FR-203 / FR-401 / FR-322)");
            player.SetSpeed(0.5);
            player.AdjustOffset(33);
            Check(engine.Speed == 0.5, "배속 0.5 적용", $"{engine.Speed}");
            Check(engine.AudioDelayMs == 33 && player.OffsetText == "+33 ms", "오프셋 +33ms 적용", player.OffsetText);

            await SeekAndSettle(12.0);
            player.SaveSlot(1);
            await SeekAndSettle(30.0);
            player.SaveSlot(3);
            await Task.Delay(200);

            Check(player.Slots[0].PositionMs == 12_000, "슬롯 1 = 12.000s", $"{player.Slots[0].PositionMs}");
            Check(player.Slots[2].PositionMs == 30_000, "슬롯 3 = 30.000s", $"{player.Slots[2].PositionMs}");
            Check(!player.Slots[1].IsSet, "슬롯 2 는 비어 있음");

            // ── 3. 탐색 ─────────────────────────────────────────────────────
            Emit("");
            Emit("3. 점프 · 프레임 스텝 (FR-303 / FR-301)");
            player.Jump(+1);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 32.5) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 32.5) < 0.001, "0.5x 에서 → = +2.5s (D-2)", Timecode.Format(engine.Position));

            async Task<long> StepsDelta(double at, int direction, int count, int gapMs, bool jumpFirst)
            {
                await SeekAndSettle(at);
                await Task.Delay(150);
                if (jumpFirst)
                {
                    player.Jump(+1);   // 곧바로(0ms) 스텝을 보낸다 — 시크 중 스텝이 버려지던 경로
                    at += JumpRules.Amount(engine.Speed).TotalSeconds;
                }
                var before = (long)Math.Round(at * 60);
                for (var i = 0; i < count; i++) { player.StepFrame(direction); if (gapMs > 0) await Task.Delay(gapMs); }
                await WaitUntil(() => engine.FrameNumber == before + direction * count, 4000);
                await Task.Delay(300);   // 더 움직이지 않는지(과잉 스텝)도 본다
                return (engine.FrameNumber ?? -1) - before;
            }

            var d = await StepsDelta(40.0, +1, 1, 0, jumpFirst: true);
            Check(d == 1, "점프 직후 곧바로 스텝 +1", $"Δ{d:+0;-0;0}");
            d = await StepsDelta(50.0, +1, 5, 0, jumpFirst: false);
            Check(d == 5, "정방향 5연속 (간격 0ms)", $"Δ{d:+0;-0;0}");
            d = await StepsDelta(50.0, -1, 5, 0, jumpFirst: false);
            Check(d == -5, "역방향 5연속 (간격 0ms)", $"Δ{d:+0;-0;0}");
            d = await StepsDelta(50.0, -1, 5, 25, jumpFirst: false);
            Check(d == -5, "역방향 5연속 (간격 25ms, 우클릭 드래그 속도)", $"Δ{d:+0;-0;0}");
            d = await StepsDelta(50.0, -1, 3, 0, jumpFirst: false) ;
            var back = await StepsDelta(50.0 - 3 / 60.0, +1, 3, 0, jumpFirst: false);
            Check(d == -3 && back == 3, "역방향 3 → 정방향 3 왕복", $"Δ{d:+0;-0;0} / Δ{back:+0;-0;0}");

            // ── 4. 입력 가드 ────────────────────────────────────────────────
            Emit("");
            Emit("4. 재생 중 단축키 차단 (FR-501 / FR-504 / D-3)");
            var source = PresentationSource.FromVisual(window)!;

            async Task Press(Key key)
            {
                foreach (var evt in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.PreviewKeyUpEvent })
                {
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = evt });
                    await Task.Delay(40);
                }
                await Task.Delay(120);
            }

            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            Check(player.IsPlaying, "재생 시작");

            await Press(Key.Space);
            await Press(Key.Left);
            await Press(Key.D1);
            Check(player.IsPlaying, "재생 중 Space · ← · 1 무시");

            await Press(Key.Escape);
            await WaitUntil(() => !player.IsPlaying);
            Check(!player.IsPlaying, "ESC 로 일시정지");

            await Press(Key.Escape);
            Check(!player.IsPlaying && shell.IsPlayerScreen, "일시정지 중 ESC 는 아무 일도 없음 (닫히지 않음)");

            await Press(Key.D3);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001, "일시정지 중 3 → 슬롯 3 으로 이동", Timecode.Format(engine.Position));

            // ── 5. 영상 위 마우스 ───────────────────────────────────────────
            Emit("");
            Emit("5. 영상 위 마우스 (SPEC §6.6)");
            var surface = window.Surface.SurfaceHandle;
            var diag = window.Surface.Diagnose();
            Check(diag.EngineWindows.Count > 0 && diag.EngineWindows.All(w => !w.Enabled), "엔진 창 비활성화됨",
                  $"{diag.EngineWindows.Count}개");
            Check(diag.PointerReachesSurface, "영상 중앙 입력이 표면으로 옴");

            PostMessage(surface, WM_LBUTTONDOWN, IntPtr.Zero, (IntPtr)((50 << 16) | 50));
            PostMessage(surface, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)((50 << 16) | 50));
            await WaitUntil(() => player.IsPlaying);
            Check(player.IsPlaying, "영상 클릭 → 재생");

            // 가장 중요한 회귀 검사: 영상을 클릭한 뒤에도 ESC 가 먹어야 한다.
            Check(Keyboard.FocusedElement is Window, "클릭 후에도 키보드 포커스가 창에 있음",
                  Keyboard.FocusedElement?.GetType().Name ?? "null");
            await Press(Key.Escape);
            await WaitUntil(() => !player.IsPlaying);
            Check(!player.IsPlaying, "클릭 후 ESC 로 일시정지");

            await SeekAndSettle(40.0);
            PostMessage(surface, WM_XBUTTONUP, (IntPtr)(1 << 16), IntPtr.Zero);        // X1 = 이전 북마크
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001, "사이드 버튼 X1 → 이전 북마크(30s)", Timecode.Format(engine.Position));

            var beforeWheel = engine.Position.TotalSeconds;
            PostMessage(surface, WM_MOUSEWHEEL, (IntPtr)(-120 << 16), IntPtr.Zero);   // 휠 아래 = 앞으로
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - (beforeWheel + 2.5)) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - (beforeWheel + 2.5)) < 0.001, "휠 아래 → +2.5s", Timecode.Format(engine.Position));

            // ── 6. 라이브러리 복귀 ──────────────────────────────────────────
            Emit("");
            Emit("6. 라이브러리 복귀 · 최근 목록 (FR-511 / FR-102)");
            var leftAt = engine.Position;
            await shell.BackToLibraryAsync();

            Check(shell.IsLibraryScreen, "라이브러리 화면으로 전환");
            var recent = shell.Library.Recent.FirstOrDefault();
            Check(shell.Library.Recent.Count == 1, "최근 목록 1건", $"{shell.Library.Recent.Count}");
            Check(recent?.BookmarkText == "🔖2", "북마크 2개 표시", recent?.BookmarkText ?? "");
            Check(recent?.OffsetText == "⏱+33 ms", "오프셋 표시", recent?.OffsetText ?? "");
            Check(recent?.SpeedText == "0.5x", "배속 표시", recent?.SpeedText ?? "");
            Check(recent?.HasThumbnail == true, "떠날 때의 프레임이 썸네일로 저장됨");
            await Task.Delay(300);
            Capture(window, "m1-library.png");

            // ── 7. 복원 ─────────────────────────────────────────────────────
            Emit("");
            Emit("7. 다시 열면 복원 (FR-103 / FR-326 / FR-404)");
            await shell.Library.OpenRecentCommand.ExecuteAsync(recent);
            await WaitUntil(() => player.HasVideo);
            await WaitUntil(() => Math.Abs((engine.Position - leftAt).TotalMilliseconds) < 1);

            Check(Math.Abs((engine.Position - leftAt).TotalMilliseconds) < 1, "마지막 위치 복원",
                  $"{Timecode.Format(leftAt)} → {Timecode.Format(engine.Position)}");
            Check(engine.Speed == 0.5 && player.Speed == 0.5, "배속 복원");
            Check(engine.AudioDelayMs == 33 && player.OffsetMs == 33, "오프셋 복원");
            Check(player.Slots[0].PositionMs == 12_000 && player.Slots[2].PositionMs == 30_000, "북마크 복원");
            Check(player.IsPaused, "복원 후 일시정지 상태");
            await Task.Delay(300);
            Capture(window, "m1-player.png");

            player.JumpToSlot(1);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 12.0) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 12.0) < 0.001 && engine.FrameNumber == 720,
                  "슬롯 1 점프 → 정확히 f#720", $"{Timecode.Format(engine.Position)} f#{engine.FrameNumber}");

            // ── 8. 영속성 ───────────────────────────────────────────────────
            Emit("");
            Emit("8. 앱 재시작에도 남는가 (NFR-08)");
            SqliteConnection.ClearAllPools();
            var reopened = new SqliteVideoLibrary(new LibraryDatabase(paths.DatabasePath));
            var list = await reopened.GetRecentAsync(10);
            var marks = list.Count == 1 ? await reopened.GetBookmarksAsync(list[0].Video.Id) : [];
            Check(list.Count == 1 && list[0].Video.AvOffsetMs == 33 && marks.Count == 2, "새 연결로 읽어도 동일",
                  $"영상 {list.Count} · 오프셋 {list.FirstOrDefault()?.Video.AvOffsetMs} · 북마크 {marks.Count}");

            // ── 9. 실패 경로 ────────────────────────────────────────────────
            Emit("");
            Emit("9. 열 수 없는 파일");
            var junk = Path.Combine(paths.DataDirectory, "not-a-video.mp4");
            await File.WriteAllBytesAsync(junk, Encoding.ASCII.GetBytes(new string('x', 4096)));
            await shell.OpenPathAsync(junk);

            Check(shell.IsLibraryScreen, "라이브러리로 돌아옴 (검은 화면에 두지 않음)");
            Check(!string.IsNullOrEmpty(shell.Notice), "오류 안내 표시", shell.Notice?.Split('\n')[0] ?? "");
        }
        catch (Exception ex)
        {
            failures++;
            Emit($"예외: {ex}");
        }

        Emit("");
        Emit("════════════════════════════════════════════════════════════");
        Emit(failures == 0 ? " 전체 통과" : $" 실패 {failures}건");
        Emit("════════════════════════════════════════════════════════════");

        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "m1-smoketest.log"), log.ToString(), Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 창의 WPF 부분을 PNG 로 남긴다. 배치가 깨졌는지 눈으로 보기 위한 것.
    /// 영상 영역은 네이티브 창이라 찍히지 않고 검게 나온다.
    /// </summary>
    private static void Capture(Window window, string fileName)
    {
        if (window.Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // 창 배경은 창 자신에 칠해져 있어 내용만 찍으면 투명(=흰색)으로 나온다. 배경을 먼저 깐다.
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(backdrop);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, fileName));
        encoder.Save(stream);
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
