using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OtogeTrainer.Playback;

/// <summary>
/// 영상이 그려질 자식 창을 WPF 에 끼워 넣고, 그 위의 마우스 입력을 제스처로 바꿔 올려 보낸다.
/// <para>
/// <b>왜 창 메시지를 직접 받는가.</b> mpv 는 <c>wid</c> 로 넘긴 창 안에 <i>자기 스레드의</i> 자식 창을
/// 만들어 그린다. 그대로 두면 영상 위의 마우스 입력이 전부 mpv 창으로 가서 WPF 는 클릭을 받지 못하고,
/// 클릭 한 번에 키보드 포커스까지 mpv 창으로 넘어가 ESC 가 먹지 않게 된다(M0 의 ClearFocus 버그와
/// 같은 증상이 다른 경로로 재발). WPF 요소를 영상 위에 겹쳐 입력을 가로채는 방법도 airspace 제약
/// 때문에 쓸 수 없다.
/// </para>
/// <para>
/// 그래서 mpv 가 만든 자식 창을 <b>비활성화</b>한다. 비활성 창은 마우스·키보드 입력을 받지 않으며
/// (<c>WindowFromPoint</c> 도 건너뛴다), 마우스 메시지는 부모인 이 창으로 떨어진다. 렌더링에는 영향이 없다.
/// </para>
/// <para>재생 중에는 키보드가 반응하지 않으므로(D-4) 여기서 올리는 제스처가 사실상 유일한 조작 경로다. (SPEC §6.6)</para>
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
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_CAPTURECHANGED = 0x0215;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;

    /// <summary>클릭으로 인정하는 최대 이동 거리(px). 이보다 움직이면 드래그로 본다.</summary>
    private const int ClickSlop = 4;

    /// <summary>우클릭 드래그에서 이만큼 움직일 때마다 한 프레임. (SPEC §6.6)</summary>
    public int PixelsPerFrame { get; set; } = 14;

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
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    private int _leftDownX, _leftDownY;
    private bool _leftDown;
    private bool _rightDragging;
    private int _dragAnchorX;

    public IntPtr SurfaceHandle { get; private set; }

    /// <summary>자식 창이 만들어져 엔진을 붙일 수 있게 된 시점.</summary>
    public event Action<IntPtr>? HandleReady;

    /// <summary>좌클릭(누르고 거의 움직이지 않고 뗌). 재생/일시정지.</summary>
    public event EventHandler? Clicked;

    /// <summary>휠. 양수 = 위(앞쪽으로 굴림) 칸 수. SPEC §6.6: 위 = 뒤로 점프.</summary>
    public event EventHandler<int>? WheelTurned;

    /// <summary>우클릭 드래그. +1 = 오른쪽(한 프레임 앞), -1 = 왼쪽.</summary>
    public event EventHandler<int>? FrameDragStep;

    /// <summary>마우스 사이드 버튼. -1 = X1(뒤로), +1 = X2(앞으로).</summary>
    public event EventHandler<int>? SideButton;

    /// <summary>
    /// 영상 위에서 마우스 버튼이 눌렸다. 호스트 창은 이때 키보드 포커스를 자기에게 다시 가져와야 한다.
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
            case WM_PARENTNOTIFY when LowWord(wParam) == WM_CREATE:
                // 엔진이 자식 창을 만들었다. 이 메시지는 엔진 스레드가 CreateWindow 안에서 응답을
                // 기다리는 동안 도착하므로, 여기서 곧바로 EnableWindow(교차 스레드 SendMessage)를
                // 부르면 교착 위험이 있다. 한 박자 늦춰서 처리한다.
                Dispatcher.InvokeAsync(() => DisableEngineWindows());
                break;

            case WM_NCHITTEST:
                handled = true;
                return HTCLIENT;

            case WM_LBUTTONDOWN:
                _leftDown = true;
                (_leftDownX, _leftDownY) = (X(lParam), Y(lParam));
                PointerPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WM_LBUTTONUP:
                if (_leftDown
                    && Math.Abs(X(lParam) - _leftDownX) <= ClickSlop
                    && Math.Abs(Y(lParam) - _leftDownY) <= ClickSlop)
                {
                    Clicked?.Invoke(this, EventArgs.Empty);
                }
                _leftDown = false;
                handled = true;
                break;

            case WM_RBUTTONDOWN:
                _rightDragging = true;
                _dragAnchorX = X(lParam);
                SetCapture(hwnd);        // 영상 밖으로 끌고 나가도 드래그가 이어지도록
                PointerPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WM_MOUSEMOVE when _rightDragging:
                StepWhileDragging(X(lParam));
                handled = true;
                break;

            case WM_RBUTTONUP:
                EndRightDrag();
                handled = true;
                break;

            case WM_CAPTURECHANGED:
                _rightDragging = false;
                break;

            case WM_CONTEXTMENU:
                handled = true;          // 우클릭은 드래그 전용. 메뉴를 띄우지 않는다.
                break;

            case WM_MOUSEWHEEL:
                RaiseWheel(wParam);
                handled = true;
                break;

            case WM_XBUTTONUP:
                SideButton?.Invoke(this, HighWord(wParam) == 1 ? -1 : +1);
                handled = true;
                return new IntPtr(1);    // XBUTTON 메시지는 TRUE 를 돌려줘야 한다
        }

        return IntPtr.Zero;
    }

    private void StepWhileDragging(int x)
    {
        var dx = x - _dragAnchorX;
        while (Math.Abs(dx) >= PixelsPerFrame)
        {
            var direction = Math.Sign(dx);
            FrameDragStep?.Invoke(this, direction);
            _dragAnchorX += direction * PixelsPerFrame;
            dx = x - _dragAnchorX;
        }
    }

    private void EndRightDrag()
    {
        _rightDragging = false;
        ReleaseCapture();
    }

    private void RaiseWheel(IntPtr wParam)
    {
        var notches = (short)HighWord(wParam) / 120;
        if (notches != 0) WheelTurned?.Invoke(this, notches);
    }

    /// <summary>
    /// 휠 메시지는 커서 아래 창이 아니라 <i>포커스를 가진</i> 창으로 가는 경우가 있다
    /// ("비활성 창 위에서 스크롤" 설정이 꺼져 있을 때). 호스트 창이 받은 휠 메시지를 넘겨주면,
    /// 커서가 영상 위에 있을 때만 처리한다.
    /// </summary>
    public bool TryHandleForwardedWheel(IntPtr wParam)
    {
        if (!IsCursorOverSurface()) return false;
        RaiseWheel(wParam);
        return true;
    }

    public bool IsCursorOverSurface()
    {
        if (SurfaceHandle == IntPtr.Zero || !GetCursorPos(out var p) || !GetWindowRect(SurfaceHandle, out var r))
            return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
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

    private static int LowWord(IntPtr v) => (int)((long)v & 0xFFFF);
    private static int HighWord(IntPtr v) => (int)(((long)v >> 16) & 0xFFFF);
    private static int X(IntPtr lParam) => (short)((long)lParam & 0xFFFF);
    private static int Y(IntPtr lParam) => (short)(((long)lParam >> 16) & 0xFFFF);
}

public sealed record SurfaceDiagnostics(
    IntPtr Surface,
    IntPtr HitTestAtCenter,
    IReadOnlyList<(IntPtr Handle, bool Enabled, bool SameThread)> EngineWindows)
{
    /// <summary>영상 중앙의 마우스 입력이 엔진 창이 아니라 이 표면으로 오는가.</summary>
    public bool PointerReachesSurface => HitTestAtCenter == Surface;
}
