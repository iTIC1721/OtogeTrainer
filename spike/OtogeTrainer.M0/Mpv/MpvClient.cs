using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OtogeTrainer.M0.Mpv;

/// <summary>libmpv client API 이벤트 ID. (client.h)</summary>
public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
}

/// <summary>
/// libmpv 최소 래퍼. M0 검증에 필요한 만큼만 노출한다.
/// <para>M1에서 <c>IMediaEngine</c> 구현으로 정리할 예정이므로 여기서는 추상화를 얹지 않는다.</para>
/// </summary>
public sealed class MpvClient : IDisposable
{
    private const string Lib = "libmpv-2.dll";

    private const int MpvFormatString = 1;
    private const int MpvFormatFlag = 3;
    private const int MpvFormatInt64 = 4;
    private const int MpvFormatDouble = 5;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(IntPtr ctx, IntPtr name, IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property_string(IntPtr ctx, IntPtr name, IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(IntPtr ctx, IntPtr name, int format, out double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(IntPtr ctx, IntPtr name, int format, out long data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(IntPtr ctx, IntPtr name, int format, out IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property(IntPtr ctx, IntPtr name, int format, ref double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_error_string(int error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(IntPtr data);

    private IntPtr _ctx;
    private Thread? _eventThread;
    private volatile bool _running;

    /// <summary>시크를 낸 시점. PlaybackRestart 까지의 왕복을 재기 위한 것. (M0-5)</summary>
    private long _seekIssuedTicks;

    public event Action<MpvEventId>? EventReceived;

    /// <summary>시크 → PlaybackRestart 왕복 시간. (M0-5)</summary>
    public event Action<double>? SeekCompleted;

    public bool IsAlive => _ctx != IntPtr.Zero;

    /// <summary>
    /// mpv를 만들고 초기화한다. <paramref name="hwnd"/> 는 임베딩 대상 자식 창. (M0-1)
    /// <para>
    /// <see cref="IntPtr.Zero"/> 를 넘기면 창 없이(헤드리스) 초기화한다. 자동 검사를
    /// GUI 없이 돌리기 위한 경로이며, 디코딩은 그대로 일어나므로 프레임 번호와
    /// 시크 정확도는 동일하게 측정된다.
    /// </para>
    /// </summary>
    public void Initialize(IntPtr hwnd)
    {
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create 실패 — libmpv-2.dll 을 불러오지 못했습니다.");

        var headless = hwnd == IntPtr.Zero;

        // SPEC §4.3 — 초기화 옵션
        if (headless)
        {
            SetOption("vo", "null");
            SetOption("ao", "null");
            SetOption("force-window", "no");
        }
        else
        {
            SetOption("wid", hwnd.ToString());
            SetOption("vo", "gpu");
        }

        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");   // ★ M0-7: mpv가 키를 직접 먹지 않게
        SetOption("osd-level", "0");
        SetOption("keep-open", "yes");
        SetOption("hr-seek", "yes");
        SetOption("audio-pitch-correction", "yes");
        SetOption("hwdec", "auto-safe");
        SetOption("keepaspect", "yes");
        SetOption("terminal", "no");

        Check(mpv_initialize(_ctx), "mpv_initialize");

        _running = true;
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-events",
        };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        while (_running)
        {
            var ptr = mpv_wait_event(_ctx, 0.1);
            if (ptr == IntPtr.Zero) continue;

            var id = (MpvEventId)Marshal.ReadInt32(ptr);
            if (id == MpvEventId.None) continue;

            if (id == MpvEventId.PlaybackRestart && _seekIssuedTicks != 0)
            {
                var ms = (Stopwatch.GetTimestamp() - _seekIssuedTicks) * 1000.0 / Stopwatch.Frequency;
                _seekIssuedTicks = 0;
                SeekCompleted?.Invoke(ms);
            }

            EventReceived?.Invoke(id);
            if (id == MpvEventId.Shutdown) break;
        }
    }

    // ── 명령 ─────────────────────────────────────────────────────────────────

    public void LoadFile(string path) => Command("loadfile", path, "replace");

    /// <summary>정확히 한 프레임 앞으로. 자동으로 일시정지된다. (FR-301)</summary>
    public void FrameStep() => Command("frame-step");

    /// <summary>정확히 한 프레임 뒤로. mpv 고유 기능이며 엔진 선택의 근거. (M0-2)</summary>
    public void FrameBackStep() => Command("frame-back-step");

    /// <summary>프레임 정확 시크. (FR-306)</summary>
    public void SeekRelativeExact(double seconds)
    {
        _seekIssuedTicks = Stopwatch.GetTimestamp();
        Command("seek", seconds.ToString("R"), "relative+exact");
    }

    public void SeekAbsoluteExact(double seconds)
    {
        _seekIssuedTicks = Stopwatch.GetTimestamp();
        Command("seek", seconds.ToString("R"), "absolute+exact");
    }

    public void Command(params string[] args)
    {
        if (!IsAlive) return;

        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[^1] = IntPtr.Zero;

            Check(mpv_command(_ctx, ptrs), $"mpv_command({string.Join(' ', args)})");
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    // ── 속성 ─────────────────────────────────────────────────────────────────

    public bool Paused
    {
        get => GetFlag("pause");
        set => SetProperty("pause", value ? "yes" : "no");
    }

    /// <summary>재생 배속. 0.10 ~ 4.00 (FR-203)</summary>
    public double Speed
    {
        get => GetDouble("speed") ?? 1.0;
        set { var v = value; SetPropertyDouble("speed", ref v); }
    }

    /// <summary>오디오 지연(초). 양수 = 소리를 늦춤. 요구사항의 "오프셋". (FR-401/402)</summary>
    public double AudioDelay
    {
        get => GetDouble("audio-delay") ?? 0.0;
        set { var v = value; SetPropertyDouble("audio-delay", ref v); }
    }

    public bool PitchCorrection
    {
        get => GetFlag("audio-pitch-correction");
        set => SetProperty("audio-pitch-correction", value ? "yes" : "no");
    }

    public double? PlaybackTime => GetDouble("playback-time");
    public double? Duration => GetDouble("duration");
    public double? ContainerFps => GetDouble("container-fps");
    public double? EstimatedVfFps => GetDouble("estimated-vf-fps");
    public long? EstimatedFrameNumber => GetInt64("estimated-frame-number");
    public long? EstimatedFrameCount => GetInt64("estimated-frame-count");
    public string? HwdecCurrent => GetString("hwdec-current");
    public string? CurrentVo => GetString("current-vo");
    public long? DisplayWidth => GetInt64("dwidth");
    public long? DisplayHeight => GetInt64("dheight");
    public string? VideoCodec => GetString("video-codec");
    public string? MpvVersion => GetString("mpv-version");
    public long? DroppedFrames => GetInt64("frame-drop-count");

    private void SetOption(string name, string value)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        var v = Marshal.StringToCoTaskMemUTF8(value);
        try { Check(mpv_set_option_string(_ctx, n, v), $"set_option({name})"); }
        finally { Marshal.FreeCoTaskMem(n); Marshal.FreeCoTaskMem(v); }
    }

    private void SetProperty(string name, string value)
    {
        if (!IsAlive) return;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        var v = Marshal.StringToCoTaskMemUTF8(value);
        try { mpv_set_property_string(_ctx, n, v); }
        finally { Marshal.FreeCoTaskMem(n); Marshal.FreeCoTaskMem(v); }
    }

    private void SetPropertyDouble(string name, ref double value)
    {
        if (!IsAlive) return;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try { mpv_set_property(_ctx, n, MpvFormatDouble, ref value); }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    private double? GetDouble(string name)
    {
        if (!IsAlive) return null;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try { return mpv_get_property(_ctx, n, MpvFormatDouble, out double v) < 0 ? null : v; }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    private long? GetInt64(string name)
    {
        if (!IsAlive) return null;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try { return mpv_get_property(_ctx, n, MpvFormatInt64, out long v) < 0 ? null : v; }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    private bool GetFlag(string name)
    {
        if (!IsAlive) return false;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try { return mpv_get_property(_ctx, n, MpvFormatFlag, out long v) >= 0 && v != 0; }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    private string? GetString(string name)
    {
        if (!IsAlive) return null;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            if (mpv_get_property(_ctx, n, MpvFormatString, out IntPtr v) < 0 || v == IntPtr.Zero)
                return null;
            var s = Marshal.PtrToStringUTF8(v);
            mpv_free(v);
            return s;
        }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    private static void Check(int code, string what)
    {
        if (code >= 0) return;
        var msg = Marshal.PtrToStringUTF8(mpv_error_string(code)) ?? code.ToString();
        throw new InvalidOperationException($"{what} 실패: {msg} ({code})");
    }

    public void Dispose()
    {
        _running = false;
        _eventThread?.Join(500);

        if (_ctx == IntPtr.Zero) return;
        mpv_terminate_destroy(_ctx);
        _ctx = IntPtr.Zero;
    }
}
