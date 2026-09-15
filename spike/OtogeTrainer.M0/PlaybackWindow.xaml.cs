using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OtogeTrainer.Input;

using OtogeTrainer.M0.Mpv;
using OtogeTrainer.Playback.Mpv;

namespace OtogeTrainer.M0;

/// <summary>
/// M0-1 ~ M0-5, M0-7, M0-8 검증 하네스.
/// <para>
/// 눈으로 보는 확인 외에, 프레임 스텝과 정확 시크는 <b>자동 검사</b>로 판정한다.
/// 타임코드가 번인된 클립을 쓰면 화면의 숫자와 엔진이 보고하는 프레임 번호를
/// 사람이 대조할 수 있지만, 누적 오차는 반복 실행으로만 드러나기 때문이다.
/// </para>
/// </summary>
public partial class PlaybackWindow : Window
{
    private readonly MpvClient _mpv = new();
    private readonly MpvVideoHost _host = new();
    private readonly DispatcherTimer _stats;
    private readonly List<double> _seekLatencies = [];
    private readonly WindowInputGuard _guard;

    private double _jumpBaseSeconds = 5.0;
    private bool _lastPlayingState;

    public PlaybackWindow()
    {
        InitializeComponent();

        // M0-7 검증 창에도 같은 가드를 건다. 이게 없으면 WPF 가 키를 먼저 먹어서
        // "mpv 가 키를 먹는지" 를 판단할 수 없다.
        _guard = new WindowInputGuard(this, isPlaying: () => _mpv.IsAlive && !_mpv.Paused);
        _guard.EscapePressed += () => { _mpv.Paused = true; SyncPlayButton(); };
        _guard.ShortcutRequested += DispatchShortcut;

        VideoHost.Child = _host;
        _host.HandleReady += hwnd => Dispatcher.BeginInvoke(() => StartMpv(hwnd));
        _mpv.SeekCompleted += ms => Dispatcher.BeginInvoke(() => _seekLatencies.Add(ms));

        _stats = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _stats.Tick += (_, _) => RenderStats();
        _stats.Start();

        Closed += (_, _) => { _stats.Stop(); _mpv.Dispose(); };
    }

    private void StartMpv(IntPtr hwnd)
    {
        try
        {
            _mpv.Initialize(hwnd);
        }
        catch (Exception ex)
        {
            TxtVerify.Text = $"mpv 초기화 실패\n\n{ex.Message}\n\n"
                           + "spike/native/libmpv-2.dll 이 출력 폴더에 복사됐는지 확인하세요.";
            TxtVerify.Foreground = (Brush)FindResource("Danger");
        }
    }

    /// <summary>
    /// M0-1 검증. 임베딩된 창에 mpv 가 실제로 렌더하는지는 속성만 봐서는 알 수 없으므로,
    /// VO 가 그린 결과를 <c>screenshot … window</c> 로 파일에 뽑아 눈으로 확인한다.
    /// </summary>
    public async Task<string> RunWindowTestAsync(string clip, string outPng)
    {
        for (var i = 0; i < 100 && !_mpv.IsAlive; i++)
            await Task.Delay(50);

        if (!_mpv.IsAlive)
            return "FAIL  mpv 초기화 실패 — HwndHost 자식 창을 얻지 못했거나 libmpv 로드 실패";

        Load(clip);
        if (!await PlaybackVerifier.WaitForLoadAsync(_mpv))
            return "FAIL  파일 로드 실패";

        _mpv.SeekAbsoluteExact(12.0);
        await Task.Delay(900);

        _mpv.Command("screenshot-to-file", outPng, "window");
        await Task.Delay(700);

        var sb = new StringBuilder();
        sb.AppendLine($"  자식 HWND        0x{_host.VideoHandle.ToInt64():X}");
        sb.AppendLine($"  current-vo       {_mpv.CurrentVo ?? "-"}");
        sb.AppendLine($"  dwidth×dheight   {_mpv.DisplayWidth?.ToString() ?? "-"}×{_mpv.DisplayHeight?.ToString() ?? "-"}");
        sb.AppendLine($"  hwdec            {_mpv.HwdecCurrent ?? "-"}");
        sb.AppendLine($"  위치             {Fmt(_mpv.PlaybackTime)}  f#{_mpv.EstimatedFrameNumber?.ToString() ?? "-"}");
        sb.AppendLine($"  드랍 프레임      {_mpv.DroppedFrames?.ToString() ?? "-"}");
        sb.AppendLine();

        var ok = File.Exists(outPng) && new FileInfo(outPng).Length > 0
                 && _mpv.CurrentVo is not null && _mpv.DisplayWidth is > 0;

        sb.AppendLine(ok
            ? $"  스크린샷 {new FileInfo(outPng).Length:N0} bytes → {outPng}"
            : "  스크린샷 생성 실패 — VO 가 렌더하지 않았을 가능성");

        return $"{(ok ? "PASS" : "FAIL")}  M0-1 HwndHost 임베딩\n{sb}";
    }

    // ── 파일 열기 ────────────────────────────────────────────────────────────

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "영상 열기",
            Filter = "영상 파일|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.flv;*.wmv|모든 파일|*.*",
        };
        if (dlg.ShowDialog(this) == true) Load(dlg.FileName);
    }

    private void OnLoadAsset(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        var path = FindAsset(name);
        if (path is null)
        {
            TxtFile.Text = $"{name} 을 찾을 수 없습니다 (tests/assets)";
            return;
        }
        Load(path);
    }

    /// <summary>출력 폴더에서 저장소 루트로 거슬러 올라가며 tests/assets 를 찾는다.</summary>
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

    private void Load(string path)
    {
        if (!_mpv.IsAlive) return;
        _mpv.LoadFile(path);
        _mpv.Paused = true;
        _seekLatencies.Clear();
        TxtFile.Text = Path.GetFileName(path);
    }

    // ── 재생 제어 ────────────────────────────────────────────────────────────

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        _mpv.Paused = !_mpv.Paused;
        SyncPlayButton();
    }

    private void SyncPlayButton()
        => BtnPlay.Content = _mpv.Paused ? "▶  재생" : "❚❚  일시정지";

    /// <summary>일시정지 상태에서만 도달한다. (재생 중에는 가드가 삼킨다)</summary>
    private void DispatchShortcut(KeyEventContext ctx)
    {
        switch (ctx.EffectiveKey)
        {
            case Key.Space: _mpv.Paused = !_mpv.Paused; SyncPlayButton(); break;
            case Key.Left: _mpv.SeekRelativeExact(-_jumpBaseSeconds * _mpv.Speed); break;
            case Key.Right: _mpv.SeekRelativeExact(_jumpBaseSeconds * _mpv.Speed); break;
            case Key.OemComma: _mpv.FrameBackStep(); break;
            case Key.OemPeriod: _mpv.FrameStep(); break;
        }
    }

    private void OnFrameForward(object sender, RoutedEventArgs e) => _mpv.FrameStep();
    private void OnFrameBack(object sender, RoutedEventArgs e) => _mpv.FrameBackStep();

    // FR-303 — 점프량 = 기준(5초) × 현재 배속 (D-2)
    private void OnSeekForward(object sender, RoutedEventArgs e)
        => _mpv.SeekRelativeExact(_jumpBaseSeconds * _mpv.Speed);

    private void OnSeekBack(object sender, RoutedEventArgs e)
        => _mpv.SeekRelativeExact(-_jumpBaseSeconds * _mpv.Speed);

    private void OnSpeed(object sender, RoutedEventArgs e)
        => _mpv.Speed = double.Parse((string)((Button)sender).Tag);

    private void OnPitchToggled(object sender, RoutedEventArgs e)
        => _mpv.PitchCorrection = ChkPitch.IsChecked == true;

    private void OnOffset(object sender, RoutedEventArgs e)
        => _mpv.AudioDelay += double.Parse((string)((Button)sender).Tag) / 1000.0;

    private void OnOffsetReset(object sender, RoutedEventArgs e) => _mpv.AudioDelay = 0;

    // ── 자동 검사 (M0-2 / M0-5) ──────────────────────────────────────────────
    //
    // 판정 로직은 PlaybackVerifier 에 있다. --selftest 헤드리스 모드와 같은 코드를
    // 써야 GUI 로 본 결과와 자동 검사 결과가 갈라지지 않는다.

    private async void OnVerifyFrameStep(object sender, RoutedEventArgs e)
    {
        if (!_mpv.IsAlive) return;
        Show(await PlaybackVerifier.VerifyFrameStepAsync(_mpv));
    }

    private async void OnVerifySeek(object sender, RoutedEventArgs e)
    {
        if (!_mpv.IsAlive) return;
        Show(await PlaybackVerifier.VerifySeekAsync(_mpv, _seekLatencies));
    }

    private void Show(VerifyReport report)
        => Report(report.Body, fail: !report.Passed,
                  header: $"{(report.Passed ? "통과" : "실패")} — {report.Title}");

    private void Report(string body, bool fail, string? header = null)
    {
        TxtVerify.Text = header is null ? body : $"{header}\n\n{body}";
        TxtVerify.Foreground = (Brush)FindResource(fail ? "Danger" : "Loop");
    }

    private void RenderStats()
    {
        if (!_mpv.IsAlive) return;

        var playing = !_mpv.Paused;
        if (playing != _lastPlayingState)
        {
            _lastPlayingState = playing;
            _guard.ApplyFocusSuppression(this);
            SyncPlayButton();
            TxtGuard.Text = playing
                ? "🔒 재생 중 · ESC 외 단축키 차단"
                : "🔓 일시정지 · 단축키 사용";
        }

        var containerFps = _mpv.ContainerFps;
        var vfFps = _mpv.EstimatedVfFps;

        // M0-8 — 컨테이너 fps 와 실측 fps 가 벌어지면 VFR 을 의심한다.
        var vfrHint = containerFps is > 0 && vfFps is > 0 && Math.Abs(containerFps.Value - vfFps.Value) > 1.0
            ? "  ⚠ VFR 의심"
            : "";

        var sb = new StringBuilder();
        sb.AppendLine($"mpv        {_mpv.MpvVersion ?? "-"}");
        sb.AppendLine($"코덱       {_mpv.VideoCodec ?? "-"}");
        sb.AppendLine($"hwdec      {_mpv.HwdecCurrent ?? "-"}");
        sb.AppendLine();
        sb.AppendLine($"위치       {Fmt(_mpv.PlaybackTime)}");
        sb.AppendLine($"길이       {Fmt(_mpv.Duration)}");
        sb.AppendLine($"프레임     f#{_mpv.EstimatedFrameNumber?.ToString() ?? "-"}"
                    + $" / {_mpv.EstimatedFrameCount?.ToString() ?? "-"}");
        sb.AppendLine();
        sb.AppendLine($"container-fps      {containerFps?.ToString("0.###") ?? "-"}");
        sb.AppendLine($"estimated-vf-fps   {vfFps?.ToString("0.###") ?? "-"}{vfrHint}");
        sb.AppendLine();
        sb.AppendLine($"배속       {_mpv.Speed:0.00}x");
        sb.AppendLine($"피치 보정  {(_mpv.PitchCorrection ? "ON" : "OFF")}");
        sb.AppendLine($"오프셋     {_mpv.AudioDelay * 1000:+0;-0;0} ms{FramesHint(containerFps)}");
        sb.AppendLine($"점프량     {_jumpBaseSeconds * _mpv.Speed:0.##}s  (5s × 배속)");
        sb.AppendLine();
        sb.AppendLine($"드랍 프레임 {_mpv.DroppedFrames?.ToString() ?? "-"}");

        TxtStats.Text = sb.ToString();
    }

    private string FramesHint(double? fps)
        => fps is > 0
            ? $"  (≈{_mpv.AudioDelay * fps.Value:0.0} frames)"
            : "";

    private static string Fmt(double? seconds)
        => seconds is null ? "-" : TimeSpan.FromSeconds(seconds.Value).ToString(@"hh\:mm\:ss\.fff");
}
