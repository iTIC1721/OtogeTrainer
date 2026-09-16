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
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

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

            // ── 5. 영상 위 마우스 — 아무 동작도 하지 않는다 (D-8) ──────────
            // 마우스를 쓰는 리듬게임을 따라 치다가 영상을 건드려도 재생이 흔들리면 안 된다.
            // "아무 일도 없었다" 가 메시지 유실 때문이 아님을 보이려고, 표면이 누름을 실제로
            // 받았는지(PointerPressed)도 함께 센다.
            Emit("");
            Emit("5. 영상 위 마우스는 아무 동작도 하지 않음 (D-8)");
            var surface = window.Surface.SurfaceHandle;
            var diag = window.Surface.Diagnose();
            Check(diag.EngineWindows.Count > 0 && diag.EngineWindows.All(w => !w.Enabled), "엔진 창 비활성화됨",
                  $"{diag.EngineWindows.Count}개");
            Check(diag.PointerReachesSurface, "영상 중앙 입력이 표면으로 옴");

            var presses = 0;
            window.Surface.PointerPressed += (_, _) => presses++;
            var center = (IntPtr)((50 << 16) | 50);

            async Task Post(int msg, IntPtr wParam, IntPtr lParam)
            {
                PostMessage(surface, msg, wParam, lParam);
                await Task.Delay(60);
            }

            // 일시정지 중 — 클릭 · 휠 · 사이드 버튼 · 우클릭 드래그 · 가운데 클릭
            await SeekAndSettle(40.0);
            var frameBefore = engine.FrameNumber;
            await Post(WM_LBUTTONDOWN, IntPtr.Zero, center);
            await Post(WM_LBUTTONUP, IntPtr.Zero, center);
            await Post(WM_MOUSEWHEEL, (IntPtr)(-120 << 16), center);
            await Post(WM_MOUSEWHEEL, (IntPtr)(120 << 16), center);
            await Post(WM_XBUTTONDOWN, (IntPtr)(1 << 16), center);
            await Post(WM_XBUTTONUP, (IntPtr)(1 << 16), center);
            await Post(WM_RBUTTONDOWN, IntPtr.Zero, center);
            await Post(WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((50 << 16) | 250));
            await Post(WM_RBUTTONUP, IntPtr.Zero, (IntPtr)((50 << 16) | 250));
            await Post(WM_MBUTTONDOWN, IntPtr.Zero, center);
            await Task.Delay(400);

            Check(presses >= 4, "표면이 마우스 누름을 실제로 받음 (대조)", $"{presses}회");
            Check(!player.IsPlaying, "클릭해도 재생되지 않음");
            Check(Math.Abs(engine.Position.TotalSeconds - 40.0) < 0.001 && engine.FrameNumber == frameBefore,
                  "휠 · 사이드 버튼 · 우클릭 드래그로 위치가 바뀌지 않음", $"{Timecode.Format(engine.Position)} f#{engine.FrameNumber}");

            // 재생 중 — 클릭해도 멈추지 않고, 클릭 뒤에도 ESC 가 먹어야 한다
            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            await Post(WM_LBUTTONDOWN, IntPtr.Zero, center);
            await Post(WM_LBUTTONUP, IntPtr.Zero, center);
            await Task.Delay(300);
            Check(player.IsPlaying, "재생 중 클릭해도 멈추지 않음");
            Check(Keyboard.FocusedElement is Window, "클릭 후에도 키보드 포커스가 창에 있음",
                  Keyboard.FocusedElement?.GetType().Name ?? "null");
            await Press(Key.Escape);
            await WaitUntil(() => !player.IsPlaying);
            Check(!player.IsPlaying, "클릭 후 ESC 로 일시정지");

            // ── 5-2. 재생 중 동작 키 (기본 F1 ~ F7) ─────────────────────────
            Emit("");
            Emit("5-2. 재생 중 동작 키 (D-8)");

            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            await Press(Key.F5);
            await WaitUntil(() => !player.IsPlaying);
            Check(!player.IsPlaying, "재생 중 F5 → 일시정지");
            await Press(Key.F5);
            await WaitUntil(() => player.IsPlaying);
            Check(player.IsPlaying, "일시정지 중 F5 → 재생");

            var playingAt = engine.Position.TotalSeconds;
            await Press(Key.F1);
            await WaitUntil(() => engine.Position.TotalSeconds < playingAt - 1.5, 2000);
            Check(engine.Position.TotalSeconds < playingAt - 1.5 && player.IsPlaying,
                  "재생 중 F1 → 뒤로 점프하고 계속 재생", $"{playingAt:0.00}s → {engine.Position.TotalSeconds:0.00}s");

            var playingFrame = engine.FrameNumber ?? 0;
            await Press(Key.F2);
            await WaitUntil(() => !player.IsPlaying, 2000);
            await Task.Delay(400);
            Check(!player.IsPlaying && engine.FrameNumber < playingFrame + 30,
                  "재생 중 F2 → 프레임 뒤로 (일시정지됨)", $"f#{engine.FrameNumber}");

            await SeekAndSettle(40.0);
            await Press(Key.F6);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001, "F6 → 이전 북마크(30s)", Timecode.Format(engine.Position));
            await Press(Key.F6);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 12.0) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 12.0) < 0.001, "F6 → 그 앞 북마크(12s)", Timecode.Format(engine.Position));
            await Press(Key.F7);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001);
            Check(Math.Abs(engine.Position.TotalSeconds - 30.0) < 0.001, "F7 → 다음 북마크(30s)", Timecode.Format(engine.Position));

            var f3Before = engine.FrameNumber ?? 0;
            await Press(Key.F3);
            await WaitUntil(() => engine.FrameNumber == f3Before + 1);
            await Press(Key.F4);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - (30.0 + 1 / 60.0 + 2.5)) < 0.01);
            Check(engine.FrameNumber is not null && Math.Abs(engine.Position.TotalSeconds - (30.0 + 1 / 60.0 + 2.5)) < 0.01,
                  "F3 프레임 앞 → F4 앞으로 점프", Timecode.Format(engine.Position));

            // 재생 중 동작 키가 아닌 F키는 여전히 막힌다
            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            await Press(Key.F8);
            await Press(Key.F9);
            Check(player.IsPlaying, "지정되지 않은 F8 · F9 는 재생 중 무시");
            player.Pause();
            await WaitUntil(() => !player.IsPlaying);

            // ── 5-3. 키 바꾸기 · 저장 ───────────────────────────────────────
            Emit("");
            Emit("5-3. 설정에서 키 바꾸기 · 저장");

            // 설정 창의 키 입력 대기를 실제 키 이벤트로 거친다
            var settingsVm = new Settings.SettingsViewModel(shell.KeyMap);
            var settingsWindow = new Settings.SettingsWindow(settingsVm) { Owner = window };
            settingsWindow.Show();
            await Task.Delay(300);
            var settingsSource = PresentationSource.FromVisual(settingsWindow)!;

            async Task PressIn(Window target, PresentationSource src, Key key)
            {
                foreach (var evt in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.PreviewKeyUpEvent })
                {
                    target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, Environment.TickCount, key) { RoutedEvent = evt });
                    await Task.Delay(40);
                }
            }

            var toggleRow = settingsVm.Bindings.First(b => b.Action == Input.PlaybackAction.TogglePlay);
            settingsVm.StartCaptureCommand.Execute(toggleRow);
            await PressIn(settingsWindow, settingsSource, Key.Escape);
            Check(!settingsVm.IsCapturing && shell.KeyMap[Input.PlaybackAction.TogglePlay] == Key.F5,
                  "입력 대기 중 ESC → 취소, 키 그대로");

            settingsVm.StartCaptureCommand.Execute(toggleRow);
            await PressIn(settingsWindow, settingsSource, Key.P);
            Check(shell.KeyMap[Input.PlaybackAction.TogglePlay] == Key.P && toggleRow.KeyDisplay == "P",
                  "입력 대기 중 P → 재생/일시정지 = P", toggleRow.KeyDisplay);
            await Task.Delay(200);
            Capture(settingsWindow, "m1-settings.png");

            settingsVm.StartCaptureCommand.Execute(toggleRow);
            await PressIn(settingsWindow, settingsSource, Key.F1);
            Check(shell.KeyMap[Input.PlaybackAction.TogglePlay] == Key.F1
                  && shell.KeyMap[Input.PlaybackAction.JumpBack] == Key.P,
                  "이미 쓰던 F1 → 뒤로 점프와 맞바꿈", settingsVm.Notice ?? "");

            settingsWindow.Close();
            await Task.Delay(200);

            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            await Press(Key.F5);
            await Task.Delay(300);
            Check(player.IsPlaying, "옛 키 F5 는 이제 재생 중 무시");
            await Press(Key.F1);
            await WaitUntil(() => !player.IsPlaying);
            Check(!player.IsPlaying, "새 키 F1 로 일시정지");

            var reloaded = new Settings.SettingsStore(paths.SettingsPath).LoadKeyMap(out var settingsError);
            Check(settingsError is null
                  && reloaded[Input.PlaybackAction.TogglePlay] == Key.F1
                  && reloaded[Input.PlaybackAction.JumpBack] == Key.P,
                  "settings.json 에 저장되어 다시 읽힘", settingsError ?? "");

            shell.KeyMap.ResetToDefaults();
            reloaded = new Settings.SettingsStore(paths.SettingsPath).LoadKeyMap(out _);
            Check(reloaded[Input.PlaybackAction.TogglePlay] == Key.F5, "기본값으로 되돌리기도 저장됨");

            await SeekAndSettle(32.5);

            // ── 5-4. 타임라인 클릭 · 드래그 (FR-202) ────────────────────────
            // 창에 실제 마우스 메시지를 보낸다. WPF 의 슬라이더 · 썸 · 마우스 캡처가 전부 실제 경로로 돈다.
            Emit("");
            Emit("5-4. 타임라인 클릭 · 드래그 (FR-202)");
            var mainHwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var timeline = window.Timeline;
            var duration = engine.Duration!.Value.TotalSeconds;

            IntPtr TimelinePoint(double fraction)
            {
                var dip = timeline.TranslatePoint(new Point(timeline.ActualWidth * fraction, timeline.ActualHeight / 2), window);
                var scale = VisualTreeHelper.GetDpi(window);
                var x = (int)Math.Round(dip.X * scale.DpiScaleX);
                var y = (int)Math.Round(dip.Y * scale.DpiScaleY);
                return (IntPtr)((y << 16) | (x & 0xFFFF));
            }

            // WPF 는 마우스 이동 메시지는 메시지의 좌표를 쓰지만, 버튼 메시지에서는 실제 커서 위치를
            // 읽는다. 메시지만 보내면 사용자의 커서가 있는 곳을 누른 것으로 처리된다(처음 이 테스트가
            // 75% 클릭을 120s 로 판정한 원인). 커서를 실제로 그 자리에 옮긴 뒤 메시지를 보낸다.
            // 클릭 자체는 이 창에만 보내므로 다른 프로그램을 누를 위험은 없다.
            GetCursorPos(out var cursorBefore);

            async Task Mouse(int msg, double fraction, bool leftDown)
            {
                var screen = timeline.PointToScreen(new Point(timeline.ActualWidth * fraction, timeline.ActualHeight / 2));
                SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
                PostMessage(mainHwnd, msg, (IntPtr)(leftDown ? 1 : 0), TimelinePoint(fraction));
                await Task.Delay(80);
            }

            // 썸(현재 위치)에서 먼 곳을 클릭해야 트랙 클릭 경로를 탄다
            await SeekAndSettle(10.0);
            await Task.Delay(200);
            var expectedClick = duration * 0.75;
            await Mouse(WM_MOUSEMOVE, 0.75, false);
            await Mouse(WM_LBUTTONDOWN, 0.75, true);
            await Mouse(WM_LBUTTONUP, 0.75, false);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - expectedClick) < 3, 2000);
            await Task.Delay(300);
            Check(Math.Abs(engine.Position.TotalSeconds - expectedClick) < 3,
                  "일시정지 중 트랙 75% 지점 클릭 → 그 위치로 이동",
                  $"기대 ≈{expectedClick:0.0}s · 실제 {engine.Position.TotalSeconds:0.0}s");

            // 트랙 아무 데서나 눌러 끌고 가서 놓기
            await SeekAndSettle(10.0);
            await Task.Delay(200);
            var expectedDrag = duration * 0.5;
            await Mouse(WM_MOUSEMOVE, 0.25, false);
            await Mouse(WM_LBUTTONDOWN, 0.25, true);
            foreach (var f in new[] { 0.3, 0.35, 0.4, 0.45, 0.5 }) await Mouse(WM_MOUSEMOVE, f, true);
            var duringDrag = engine.Position.TotalSeconds;
            await Mouse(WM_LBUTTONUP, 0.5, false);
            await WaitUntil(() => Math.Abs(engine.Position.TotalSeconds - expectedDrag) < 3, 2000);
            await Task.Delay(300);
            Check(Math.Abs(engine.Position.TotalSeconds - expectedDrag) < 3,
                  "25% 에서 눌러 50% 까지 끌고 놓기 → 50% 위치",
                  $"기대 ≈{expectedDrag:0.0}s · 실제 {engine.Position.TotalSeconds:0.0}s");
            Check(duringDrag > 20, "끄는 도중에도 영상이 따라옴", $"드래그 중 {duringDrag:0.0}s");

            // 재생 중에도
            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            var expectedPlaying = duration * 0.4;
            await Mouse(WM_MOUSEMOVE, 0.4, false);
            await Mouse(WM_LBUTTONDOWN, 0.4, true);
            await Mouse(WM_LBUTTONUP, 0.4, false);
            await Task.Delay(500);
            Check(Math.Abs(engine.Position.TotalSeconds - expectedPlaying) < 3 && player.IsPlaying,
                  "재생 중 40% 지점 클릭 → 이동하고 계속 재생",
                  $"기대 ≈{expectedPlaying:0.0}s · 실제 {engine.Position.TotalSeconds:0.0}s");
            Check(Keyboard.FocusedElement is Window, "타임라인을 만진 뒤에도 포커스가 창에 있음 (ESC)",
                  Keyboard.FocusedElement?.GetType().Name ?? "null");
            await Press(Key.Escape);
            await WaitUntil(() => !player.IsPlaying);
            SetCursorPos(cursorBefore.X, cursorBefore.Y);   // 사용자 커서를 원래 자리로
            await SeekAndSettle(32.5);

            // ── 5-5. 전체화면 (FR-207) ──────────────────────────────────────
            Emit("");
            Emit("5-5. 전체화면 (FR-207)");
            var windowedWidth = window.ActualWidth;
            var windowedSurface = window.Surface.ActualWidth;

            shell.ToggleFullscreenCommand.Execute(null);
            await Task.Delay(700);
            var workArea = SystemParameters.PrimaryScreenWidth;
            Check(window.WindowStyle == WindowStyle.None && window.WindowState == WindowState.Maximized,
                  "버튼 → 테두리 없는 최대화", $"{window.WindowStyle} · {window.WindowState}");
            Check(window.Surface.ActualWidth > windowedSurface + 20,
                  "영상이 커짐", $"{windowedSurface:0} → {window.Surface.ActualWidth:0} (화면 {workArea:0})");
            Check(Keyboard.FocusedElement is Window, "전환 후에도 포커스가 창에 있음",
                  Keyboard.FocusedElement?.GetType().Name ?? "null");
            await Task.Delay(300);
            Capture(window, "m1-fullscreen.png");

            player.TogglePlay();
            await WaitUntil(() => player.IsPlaying);
            await Press(Key.F11);
            await Task.Delay(700);
            Check(!shell.IsFullscreen && window.WindowStyle != WindowStyle.None && player.IsPlaying,
                  "재생 중 F11 → 창 모드로, 재생은 계속", $"{window.WindowStyle} · 재생 {player.IsPlaying}");
            Check(Math.Abs(window.ActualWidth - windowedWidth) < 2,
                  "창 크기가 원래대로", $"{windowedWidth:0} → {window.ActualWidth:0}");
            await Press(Key.F11);
            await Task.Delay(700);
            Check(shell.IsFullscreen && player.IsPlaying, "재생 중 F11 → 다시 전체화면");
            await Press(Key.Escape);
            await WaitUntil(() => !player.IsPlaying);
            Check(shell.IsFullscreen && !player.IsPlaying, "ESC 는 일시정지만 (전체화면 유지, D-3)");
            await SeekAndSettle(32.5);

            // ── 6. 라이브러리 복귀 ──────────────────────────────────────────
            Emit("");
            Emit("6. 라이브러리 복귀 · 최근 목록 (FR-511 / FR-102)");
            var leftAt = engine.Position;
            await shell.BackToLibraryAsync();

            Check(shell.IsLibraryScreen, "라이브러리 화면으로 전환");
            await Task.Delay(500);
            Check(!shell.IsFullscreen && window.WindowStyle != WindowStyle.None,
                  "전체화면에서 복귀하면 창 모드로", $"{window.WindowStyle}");
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

            // ── 10. 창 닫기 ─────────────────────────────────────────────────
            // 라이브러리 화면에서 닫으면 저장할 것이 없어 종료 저장이 동기로 끝난다. 그 경로에서
            // Closing 도중 Close() 를 다시 불러 예외가 났었다(수동 사용 중 발견).
            Emit("");
            Emit("10. 창 닫기");
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 닫혀도 검증은 계속
            var crashLog = Path.Combine(paths.LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var crashBefore = File.Exists(crashLog) ? new FileInfo(crashLog).Length : 0;

            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            await WaitUntil(() => closed, 3000);
            await Task.Delay(300);

            var crashAfter = File.Exists(crashLog) ? new FileInfo(crashLog).Length : 0;
            Check(closed && window.TestShutdownSaved, "라이브러리 화면에서 창이 닫힘");
            Check(crashAfter == crashBefore, "닫는 중 예외 없음",
                  crashAfter == crashBefore ? "" : File.ReadAllLines(crashLog).FirstOrDefault(l => l.Contains("Exception")) ?? "");
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
        // 창 내용(Content)이 아니라 창 템플릿의 최상위를 찍는다. 내용에 Margin 이 있으면
        // 그만큼 밀려서 오른쪽 · 아래가 잘려 찍힌다.
        if (VisualTreeHelper.GetChildrenCount(window) == 0
            || VisualTreeHelper.GetChild(window, 0) is not FrameworkElement root) return;
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
