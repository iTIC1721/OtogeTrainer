using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OtogeTrainer.App.Settings;

public partial class SettingsWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;

    private readonly SettingsViewModel _vm;
    private bool _swallowKeyUp;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => _vm.CancelCapture();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    /// <summary>
    /// 입력 대기 중이면 어떤 키든 가로챈다. Tab · 방향키 · Space 가 포커스 이동이나 버튼 클릭으로
    /// 새지 않도록 전부 소비한다.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.IsCapturing) return;

        // Alt · F10 은 System 으로, IME 가 켜진 상태의 문자 키는 ImeProcessed 로 감싸여 온다.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        if (_vm.CompleteCapture(key))
        {
            e.Handled = true;
            _swallowKeyUp = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_swallowKeyUp) return;
        e.Handled = true;

        // Alt 를 떼면 이 KeyUp 처리가 끝난 뒤에 DefWindowProc 이 SC_KEYMENU 를 보낸다.
        // 여기서 바로 내리면 그 메시지를 못 막으므로 한 박자 늦춘다.
        Dispatcher.BeginInvoke(() => _swallowKeyUp = false, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Alt 나 F10 을 지정하려고 눌렀을 때 창 시스템 메뉴가 열리지 않게 한다.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && ((long)wParam & 0xFFF0) == SC_KEYMENU && lParam == IntPtr.Zero
            && (_vm.IsCapturing || _swallowKeyUp))
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ── 자동 검증용 (--smoketest) ────────────────────────────────────────────

    internal SettingsViewModel ViewModel => _vm;
}
