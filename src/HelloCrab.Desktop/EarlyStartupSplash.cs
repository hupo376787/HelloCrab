using System.Runtime.InteropServices;

namespace HelloCrab.Desktop;

/// <summary>
/// Windows 冷启动阶段使用的单一原生启动闪屏。
/// 它从进程入口立即出现，并一直保留到主窗口显示，避免原生闪屏和
/// Avalonia 闪屏先后出现造成两个启动界面。
/// </summary>
internal sealed class EarlyStartupSplash : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsClipChildren = 0x02000000;

    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExTransparent = 0x00000020;

    private const uint SsLeft = 0x00000000;
    private const uint SsCenter = 0x00000001;
    private const uint SsRight = 0x00000002;

    private const uint PbsSmooth = 0x00000001;

    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 0x0001;
    private const uint WmClose = 0x0010;
    private const uint WmSetFont = 0x0030;
    private const uint WmCtlColorStatic = 0x0138;
    private const int TransparentBkMode = 1;

    private const uint PbmSetPos = 0x0402;
    private const uint PbmSetRange32 = 0x0406;
    private const uint PbmSetBarColor = 0x0409;
    private const uint PbmSetBkColor = 0x2001;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint IccProgressClass = 0x00000020;

    private const int WindowWidth = 560;
    private const int WindowHeight = 330;

    private static readonly WndProcDelegate WindowProcDelegate = WindowProc;
    private static nint _backgroundBrush;

    private readonly Thread _thread;
    private readonly string _windowClassName;
    private readonly ManualResetEventSlim _ready = new(false);

    private volatile bool _closing;
    private nint _window;
    private nint _statusText;
    private nint _detailText;
    private nint _percentText;
    private nint _progressBar;

    private EarlyStartupSplash()
    {
        ShownAt = DateTimeOffset.UtcNow;
        _windowClassName = $"HelloCrab.StartupSplash.{Environment.ProcessId}";
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "HelloCrab Startup Splash"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 不长时间阻塞进程入口，只给原生窗口线程一个很短的创建机会。
        _ready.Wait(TimeSpan.FromMilliseconds(120));
    }

    public DateTimeOffset ShownAt { get; }

    public static EarlyStartupSplash? TryStart()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return new EarlyStartupSplash();
        }
        catch
        {
            return null;
        }
    }

    public void SetProgress(double percent, string status, string? detail = null)
    {
        var value = (int)Math.Round(Math.Clamp(percent, 0d, 100d));

        var statusHandle = _statusText;
        if (statusHandle != 0)
            SetWindowTextW(statusHandle, status);

        var detailHandle = _detailText;
        if (detailHandle != 0)
            SetWindowTextW(detailHandle, detail ?? string.Empty);

        var percentHandle = _percentText;
        if (percentHandle != 0)
            SetWindowTextW(percentHandle, $"{value}%");

        var progressHandle = _progressBar;
        if (progressHandle != 0)
            _ = SendMessageW(progressHandle, PbmSetPos, (nint)value, 0);

        var window = _window;
        if (window != 0)
            UpdateWindow(window);
    }

    public void Dispose()
    {
        if (_closing)
            return;

        _closing = true;
        var window = _window;
        if (window != 0)
            PostMessageW(window, WmClose, 0, 0);
    }

    private void Run()
    {
        nint backgroundBrush = 0;
        nint titleFont = 0;
        nint normalFont = 0;
        nint smallFont = 0;
        ushort classAtom = 0;

        try
        {
            var init = new InitCommonControlsData
            {
                Size = (uint)Marshal.SizeOf<InitCommonControlsData>(),
                Icc = IccProgressClass
            };
            _ = InitCommonControlsEx(ref init);

            var instance = GetModuleHandleW(null);
            backgroundBrush = CreateSolidBrush(ToColorRef(0xF9, 0xFA, 0xFE));
            _backgroundBrush = backgroundBrush;

            var windowClass = new WindowClassExData
            {
                Size = (uint)Marshal.SizeOf<WindowClassExData>(),
                Instance = instance,
                BackgroundBrush = backgroundBrush,
                ClassName = _windowClassName,
                WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate)
            };
            classAtom = RegisterClassExW(ref windowClass);
            if (classAtom == 0)
                return;

            var left = Math.Max(0, (GetSystemMetrics(SmCxScreen) - WindowWidth) / 2);
            var top = Math.Max(0, (GetSystemMetrics(SmCyScreen) - WindowHeight) / 2);

            var window = CreateWindowExW(
                WsExTopmost | WsExToolWindow | WsExNoActivate,
                _windowClassName,
                string.Empty,
                WsPopup | WsVisible | WsClipChildren,
                left,
                top,
                WindowWidth,
                WindowHeight,
                0,
                0,
                instance,
                0);

            if (window == 0)
                return;

            _window = window;

            var roundedRegion = CreateRoundRectRgn(
                0,
                0,
                WindowWidth + 1,
                WindowHeight + 1,
                24,
                24);
            if (roundedRegion != 0)
                _ = SetWindowRgn(window, roundedRegion, true);

            titleFont = CreateFontW(
                -30, 0, 0, 0, 600,
                0, 0, 0, 1, 0, 0, 5, 0,
                "Segoe UI");
            normalFont = CreateFontW(
                -16, 0, 0, 0, 400,
                0, 0, 0, 1, 0, 0, 5, 0,
                "Microsoft YaHei UI");
            smallFont = CreateFontW(
                -14, 0, 0, 0, 400,
                0, 0, 0, 1, 0, 0, 5, 0,
                "Microsoft YaHei UI");

            var title = CreateLabel(
                window,
                "HelloCrab",
                36,
                42,
                488,
                40,
                SsCenter,
                titleFont);

            var subtitle = CreateLabel(
                window,
                "正在准备应用，请稍候",
                36,
                82,
                488,
                26,
                SsCenter,
                normalFont);

            _statusText = CreateLabel(
                window,
                "正在启动 HelloCrab…",
                36,
                134,
                430,
                24,
                SsLeft,
                normalFont);

            _percentText = CreateLabel(
                window,
                "0%",
                468,
                134,
                56,
                24,
                SsRight,
                normalFont);

            _progressBar = CreateWindowExW(
                0,
                "msctls_progress32",
                string.Empty,
                WsChild | WsVisible | PbsSmooth,
                36,
                165,
                488,
                8,
                window,
                0,
                instance,
                0);

            if (_progressBar != 0)
            {
                _ = SendMessageW(_progressBar, PbmSetRange32, 0, (nint)100);
                _ = SendMessageW(
                    _progressBar,
                    PbmSetBkColor,
                    0,
                    (nint)ToColorRef(0xE3, 0xE6, 0xF0));
                _ = SendMessageW(
                    _progressBar,
                    PbmSetBarColor,
                    0,
                    (nint)ToColorRef(0x7C, 0x3A, 0xED));
                _ = SendMessageW(_progressBar, PbmSetPos, (nint)3, 0);
            }

            _detailText = CreateLabel(
                window,
                "准备应用运行环境",
                36,
                186,
                488,
                42,
                SsLeft,
                smallFont);

            _ = title;
            _ = subtitle;

            ShowWindow(window, SwShowNoActivate);
            UpdateWindow(window);
            _ready.Set();

            while (!_closing && IsWindow(window))
            {
                while (PeekMessageW(out var message, 0, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                Thread.Sleep(8);
            }

            if (IsWindow(window))
                DestroyWindow(window);

            _window = 0;
        }
        catch
        {
            _window = 0;
        }
        finally
        {
            _ready.Set();

            if (titleFont != 0)
                DeleteObject(titleFont);
            if (normalFont != 0)
                DeleteObject(normalFont);
            if (smallFont != 0)
                DeleteObject(smallFont);

            if (classAtom != 0)
                UnregisterClassW(_windowClassName, GetModuleHandleW(null));

            if (backgroundBrush != 0)
                DeleteObject(backgroundBrush);

            _backgroundBrush = 0;
        }
    }

    private static nint CreateLabel(
        nint parent,
        string text,
        int x,
        int y,
        int width,
        int height,
        uint alignment,
        nint font)
    {
        var handle = CreateWindowExW(
            WsExTransparent,
            "STATIC",
            text,
            WsChild | WsVisible | alignment,
            x,
            y,
            width,
            height,
            parent,
            0,
            GetModuleHandleW(null),
            0);

        if (handle != 0 && font != 0)
            _ = SendMessageW(handle, WmSetFont, font, (nint)1);

        return handle;
    }

    private static nint WindowProc(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam)
    {
        if (message == WmCtlColorStatic)
        {
            var hdc = (nint)wParam;
            _ = SetBkMode(hdc, TransparentBkMode);
            _ = SetTextColor(hdc, ToColorRef(0x17, 0x20, 0x33));
            return _backgroundBrush;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static uint ToColorRef(byte red, byte green, byte blue)
        => (uint)(red | (green << 8) | (blue << 16));

    [StructLayout(LayoutKind.Sequential)]
    private struct InitCommonControlsData
    {
        public uint Size;
        public uint Icc;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassExData
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    private delegate nint WndProcDelegate(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitCommonControlsEx(ref InitCommonControlsData init);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassExData windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint hwnd, string text);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out Msg message,
        nint hwnd,
        uint minFilter,
        uint maxFilter,
        uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint colorRef);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
