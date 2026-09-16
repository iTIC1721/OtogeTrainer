using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OtogeTrainer.Playback;

/// <summary>
/// 영상이 그려질 자식 창을 WPF 에 끼워 넣는다.
/// <para>
/// <b>영상 위 마우스 입력은 아무 동작도 하지 않는다(D-8).</b> 마우스를 쓰는 리듬게임이 있어서,
/// 따라 치다가 영상을 건드려도 재생이 멈추거나 위치가 튀면 안 된다. 클릭 · 휠 · 우클릭 · 사이드 버튼을
/// 전부 받아서 버린다. 재생 중 조작은 키보드의 재생 중 동작 키로 한다.
/// </para>
/// <para>
/// <b>그래도 입력은 이 창이 받아야 한다.</b> mpv 는 <c>wid</c> 로 넘긴 창 안에 <i>자기 스레드의</i> 자식 창을
/// 만들어 그린다. 그대로 두면 영상을 클릭하는 순간 키보드 포커스가 그 창으로 넘어가 ESC 도 재생 중
/// 동작 키도 먹지 않게 된다(M0 의 ClearFocus 버그와 같은 증상). 그래서 mpv 자식 창을 <b>비활성화</b>해
/// 입력이 이 창으로 떨어지게 하고, 누르는 순간 호스트 창이 포커스를 되찾게 알린다.
/// </para>
/// </summary>
public sealed class VideoSurfaceHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int SS_NOTIFY = 0x00000100;

    private const int WM_CREATE = 0x0001;
    private const int WM_PARENTNOTIFY = 0x0210;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_MOUSEFIRST = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int HTCLIENT = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    public IntPtr SurfaceHandle { get; private set; }

    /// <summary>자식 창이 만들어져 엔진을 붙일 수 있게 된 시점.</summary>
    public event Action<IntPtr>? HandleReady;

    /// <summary>
    /// 영상 위에서 마우스 버튼이 눌렸다. 이것으로 아무 동작도 하지 않는다 —
    /// 호스트 창은 이때 키보드 포커스를 자기에게 되돌리기만 한다.
    /// </summary>
    public event EventHandler? PointerPressed;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // "static" 은 윈도우가 기본 제공하는 창 클래스. SS_NOTIFY 가 있어야 마우스 메시지를 받는다
        // (없으면 WM_NCHITTEST 에 HTTRANSPARENT 를 돌려줘 부모로 흘려보낸다).
        SurfaceHandle = CreateWindowExW(
            0, "static", null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_NOTIFY,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (SurfaceHandle == IntPtr.Zero)
            throw new InvalidOperationException($"영상 표면 창 생성 실패 (Win32 {Marshal.GetLastWin32Error()})");

        HandleReady?.Invoke(SurfaceHandle);
        return new HandleRef(this, SurfaceHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        SurfaceHandle = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_PARENTNOTIFY when ((long)wParam & 0xFFFF) == WM_CREATE:
                // 엔진이 자식 창을 만들었다. 이 메시지는 엔진 스레드가 CreateWindow 안에서 응답을
                // 기다리는 동안 도착하므로, 여기서 곧바로 EnableWindow(교차 스레드 SendMessage)를
                // 부르면 교착 위험이 있다. 한 박자 늦춰서 처리한다.
                Dispatcher.InvokeAsync(() => DisableEngineWindows());
                return IntPtr.Zero;

            case WM_NCHITTEST:
                handled = true;
                return HTCLIENT;

            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN:
                PointerPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
                return msg == WM_XBUTTONDOWN ? new IntPtr(1) : IntPtr.Zero;

            case WM_XBUTTONUP:
                handled = true;
                return new IntPtr(1);        // XBUTTON 메시지는 TRUE 를 돌려줘야 한다

            case WM_CONTEXTMENU:
                handled = true;              // 부모로 올라가 메뉴가 뜨지 않게
                return IntPtr.Zero;

            case >= WM_MOUSEFIRST and <= WM_MOUSEHWHEEL:
                handled = true;              // 이동 · 뗌 · 휠 등 나머지 마우스 입력도 전부 버린다
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>엔진이 만든 자식 창을 전부 비활성화한다. 몇 개를 비활성화했는지 돌려준다.</summary>
    public int DisableEngineWindows() => SetEngineWindowsEnabled(false);

    /// <summary>
    /// 진단 전용. 엔진 창을 다시 활성화해 대조군을 만든다 — 비활성화가 실제로 차이를 만드는지
    /// 확인하려면 "비활성화하지 않았을 때 입력이 엔진 창으로 간다" 는 것도 보여야 한다.
    /// </summary>
    public int EnableEngineWindowsForDiagnostics() => SetEngineWindowsEnabled(true);

    private int SetEngineWindowsEnabled(bool enabled)
    {
        if (SurfaceHandle == IntPtr.Zero) return 0;

        var count = 0;
        EnumChildWindows(SurfaceHandle, (child, lp) =>
        {
            if (IsWindowEnabled(child) != enabled) { EnableWindow(child, enabled); count++; }
            return true;
        }, IntPtr.Zero);
        return count;
    }

    /// <summary>진단용. 영상 중앙에 마우스가 있을 때 시스템이 입력을 보낼 창과 그 상태.</summary>
    public SurfaceDiagnostics Diagnose()
    {
        var children = new List<(IntPtr Handle, bool Enabled, bool SameThread)>();
        var myThread = GetWindowThreadProcessId(SurfaceHandle, out _);

        EnumChildWindows(SurfaceHandle, (child, lp) =>
        {
            children.Add((child, IsWindowEnabled(child), GetWindowThreadProcessId(child, out _) == myThread));
            return true;
        }, IntPtr.Zero);

        IntPtr hit = IntPtr.Zero;
        if (GetWindowRect(SurfaceHandle, out var r))
            hit = WindowFromPoint(new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 });

        return new SurfaceDiagnostics(SurfaceHandle, hit, children);
    }
}

public sealed record SurfaceDiagnostics(
    IntPtr Surface,
    IntPtr HitTestAtCenter,
    IReadOnlyList<(IntPtr Handle, bool Enabled, bool SameThread)> EngineWindows)
{
    /// <summary>영상 중앙의 마우스 입력이 엔진 창이 아니라 이 표면으로 오는가.</summary>
    public bool PointerReachesSurface => HitTestAtCenter == Surface;
}
