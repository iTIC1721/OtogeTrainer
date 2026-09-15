using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OtogeTrainer.Playback.Mpv;

/// <summary>
/// libmpv 를 담을 자식 HWND 를 만들어 WPF 비주얼 트리에 끼워 넣는다. (M0-1)
/// <para>
/// mpv 는 <c>--wid=&lt;HWND&gt;</c> 로 임의의 창에 렌더한다. WPF 쪽에서 필요한 것은
/// <see cref="HwndHost"/> 가 관리하는 자식 창 핸들 하나뿐이다.
/// </para>
/// </summary>
public sealed class MpvVideoHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    public IntPtr VideoHandle { get; private set; }

    /// <summary>자식 창이 만들어져 mpv 를 붙일 수 있게 된 시점.</summary>
    public event Action<IntPtr>? HandleReady;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // "static" 은 윈도우가 기본 제공하는 창 클래스다. 렌더 대상만 필요하므로
        // 별도 클래스를 등록할 이유가 없다.
        VideoHandle = CreateWindowExW(
            0, "static", null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (VideoHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"자식 창 생성 실패 (Win32 {Marshal.GetLastWin32Error()})");

        HandleReady?.Invoke(VideoHandle);
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle == IntPtr.Zero) return;
        DestroyWindow(hwnd.Handle);
        VideoHandle = IntPtr.Zero;
    }
}
