using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using OtogeTrainer.App.Library;
using OtogeTrainer.App.Player;
using OtogeTrainer.Data;
using OtogeTrainer.Playback;

namespace OtogeTrainer.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private MpvMediaEngine? _engine;
    private AppPaths? _paths;

    /// <summary>
    /// 실행 인자
    /// <list type="bullet">
    /// <item><c>&lt;영상 경로&gt;</c> — 바로 연다 ("연결 프로그램으로 열기")</item>
    /// <item><c>--data &lt;폴더&gt;</c> — 데이터 위치를 바꾼다 (기본 %APPDATA%\OtogeTrainer)</item>
    /// <item><c>--smoketest [영상]</c> — 종단 자동 검증. 데이터는 임시 폴더를 쓴다</item>
    /// </list>
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var smokeTest = args.Contains("--smoketest");
        if (smokeTest) AttachConsole(-1);

        // 자동 검증이 사용자의 실제 라이브러리를 건드리면 안 된다.
        var dataDir = ArgValue(args, "--data")
                      ?? (smokeTest ? Path.Combine(Path.GetTempPath(), "otoge-smoke-" + Guid.NewGuid().ToString("N")) : null);
        _paths = dataDir is null ? AppPaths.Default() : new AppPaths(dataDir);

        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception);
            MessageBox.Show(
                $"예상하지 못한 오류가 발생했습니다.\n\n{ex.Exception.Message}\n\n로그: {_paths.LogDirectory}",
                "OtogeTrainer", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var db = new LibraryDatabase(_paths.DatabasePath);
        try
        {
            await db.MigrateAsync();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show(ex.Message, "OtogeTrainer — 라이브러리를 열 수 없습니다",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var library = new SqliteVideoLibrary(db);
        var surface = new VideoSurfaceHost();
        _engine = new MpvMediaEngine(surface);

        var player = new PlayerViewModel(_engine, library, _paths);
        var shell = new ShellViewModel(new LibraryViewModel(library, _paths), player, library);
        var window = new MainWindow(shell, surface);
        MainWindow = window;
        window.Show();

        if (smokeTest)
        {
            var code = await SmokeTest.RunAsync(window, shell, _paths, args);
            Shutdown(code);
            return;
        }

        if (args.FirstOrDefault(a => !a.StartsWith("--") && AppPaths.IsVideoFile(a) && File.Exists(a)) is { } file)
            await shell.OpenPathAsync(file);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        base.OnExit(e);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void LogCrash(Exception ex)
    {
        try
        {
            if (_paths is null) return;
            var file = Path.Combine(_paths.LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"[{DateTime.Now:O}] {ex}\n\n", Encoding.UTF8);
        }
        catch (IOException)
        {
            // 로그를 못 남겨도 원래 오류 보고를 가리지 않는다
        }
    }
}
