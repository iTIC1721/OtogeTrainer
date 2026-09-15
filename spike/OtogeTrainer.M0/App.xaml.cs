using System.Runtime.InteropServices;
using System.Windows;

namespace OtogeTrainer.M0;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WinExe 는 콘솔이 없다. 부모 콘솔에 붙여야 stdout 이 보인다.
        if (e.Args.Any(a => a.StartsWith("--"))) AttachConsole(AttachParentProcess);

        if (e.Args.Contains("--selftest"))
        {
            Shutdown(await SelfTest.RunAsync(e.Args));
            return;
        }

        if (e.Args.Contains("--inputtest"))
        {
            Shutdown(await SelfTest.RunInputTestAsync());
            return;
        }

        if (e.Args.Contains("--windowtest"))
        {
            Shutdown(await SelfTest.RunWindowTestAsync(e.Args));
            return;
        }

        new MainWindow().Show();
    }
}
