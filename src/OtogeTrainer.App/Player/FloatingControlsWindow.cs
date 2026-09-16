using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OtogeTrainer.App.Player;

/// <summary>
/// 전체화면에서 영상 위에 떠 있는 반투명 컨트롤 바. (FR-207)
/// <para>
/// 영상은 네이티브 창(HwndHost)이라 같은 창 안의 WPF 요소를 그 위에 겹칠 수 없다(airspace).
/// 그래서 투명한 <b>별도 창</b>으로 띄운다. 창 사이의 합성은 DWM 이 하므로 영상 위에 반투명으로 보인다.
/// </para>
/// <para>
/// ★ 이 창은 절대 활성화되거나 키보드 포커스를 가져가면 안 된다. 가져가면 주 창이 비활성화되어
/// 일시정지되고(FR-507), ESC 와 재생 중 동작 키가 주 창의 입력 가드에 닿지 않는다.
/// <c>WS_EX_NOACTIVATE</c> 로 클릭해도 활성화되지 않게 하고, WPF 안에서의 포커스 이동도 막는다.
/// </para>
/// </summary>
internal sealed class FloatingControlsWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    /// <summary>화면 가장자리와 바 사이 여백.</summary>
    public const double EdgeMargin = 24;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private readonly ContentControl _host = new() { Focusable = false };

    public FloatingControlsWindow(Window owner)
    {
        Owner = owner;
        DataContext = owner.DataContext;   // 컨트롤 안의 AncestorType=Window 바인딩(전체화면 버튼 등)이 셸을 보게
        FontFamily = owner.FontFamily;
        UseLayoutRounding = true;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.Height;

        // 컨트롤 안의 AncestorType=UserControl 바인딩(북마크 슬롯 등)이 플레이어를 찾도록 한 겹 감싼다
        Content = new UserControl
        {
            Focusable = false,
            Padding = new Thickness(EdgeMargin, 0, EdgeMargin, EdgeMargin),
            Content = _host,
        };

        // 버튼을 누르면 WPF 가 버튼에 포커스를 주려 한다. 여기서 막아야 Win32 포커스도 옮겨지지 않는다
        PreviewGotKeyboardFocus += (_, e) => e.Handled = true;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
    }

    /// <summary>띄울 컨트롤. 플레이어 화면에서 떼어 온 요소 그대로다.</summary>
    public void SetControls(FrameworkElement controls, object playerDataContext)
    {
        ((UserControl)Content).DataContext = playerDataContext;
        _host.Content = controls;
    }

    public void ClearControls() => _host.Content = null;

    /// <summary>주 창의 화면 영역(DIP) 아래쪽에 맞춘다.</summary>
    public void PlaceAtBottom(Rect area)
    {
        Width = area.Width;
        Left = area.Left;
        UpdateLayout();
        Top = area.Bottom - ActualHeight;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }
}
