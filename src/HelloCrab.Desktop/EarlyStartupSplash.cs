using System.Runtime.InteropServices;

namespace HelloCrab.Desktop;

/// <summary>
/// Windows 冷启动阶段的极轻量原生占位闪屏。
/// Avalonia 初始化完成并绘制正式 SplashWindow 后立即关闭。
/// 非 Windows 平台不创建该窗口。
/// </summary>
internal sealed class EarlyStartupSplash : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsBorder = 0x00800000;
    private const uint WsChild = 0x40000000;

    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint SsCenter = 0x00000001;
    private const uint PbsMarquee = 0x00000008;

    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 0x0001;
    private const uint WmClose = 0x0010;
    private const uint WmSetFont = 0x0030;
    private const uint PbmSetMarquee = 0x040A;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint IccProgressClass = 0x00000020;

    private readonly Thread _thread;
    private volatile bool _closing;
    private nint _window;

    private EarlyStartupSplash()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "HelloCrab Early Splash"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

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
            // 原生占位闪屏失败不应影响应用本身启动。
            return null;
        }
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
        try
        {
            var init = new InitCommonControlsEx
            {
                Size = (uint)Marshal.SizeOf<InitCommonControlsEx>(),
                Icc = IccProgressClass
            };
            _ = InitCommonControlsEx(ref init);

            const int width = 480;
            const int height = 220;
            var left = Math.Max(0, (GetSystemMetrics(SmCxScreen) - width) / 2);
            var top = Math.Max(0, (GetSystemMetrics(SmCyScreen) - height) / 2);

            var window = CreateWindowExW(
                WsExTopmost | WsExToolWindow | WsExNoActivate,
                "STATIC",
                string.Empty,
                WsPopup | WsVisible | WsBorder,
                left,
                top,
                width,
                height,
                0,
                0,
                0,
                0);

            if (window == 0)
                return;

            _window = window;

            var title = CreateWindowExW(
                0,
                "STATIC",
                "HelloCrab",
                WsChild | WsVisible | SsCenter,
                40,
                54,
                400,
                34,
                window,
                0,
                0,
                0);

            var subtitle = CreateWindowExW(
                0,
                "STATIC",
                "正在启动，请稍候…",
                WsChild | WsVisible | SsCenter,
                40,
                96,
                400,
                24,
                window,
                0,
                0,
                0);

            var progress = CreateWindowExW(
                0,
                "msctls_progress32",
                string.Empty,
                WsChild | WsVisible | PbsMarquee,
                55,
                148,
                370,
                8,
                window,
                0,
                0,
                0);

            var titleFont = CreateFontW(
                27, 0, 0, 0, 600,
                0, 0, 0, 1, 0, 0, 5, 0,
                "Segoe UI");
            var textFont = CreateFontW(
                16, 0, 0, 0, 400,
                0, 0, 0, 1, 0, 0, 5, 0,
                "Microsoft YaHei UI");

            if (titleFont != 0 && title != 0)
                _ = SendMessageW(title, WmSetFont, titleFont, 1);
            if (textFont != 0 && subtitle != 0)
                _ = SendMessageW(subtitle, WmSetFont, textFont, 1);
            if (progress != 0)
                _ = SendMessageW(progress, PbmSetMarquee, 1, 28);

            ShowWindow(window, SwShowNoActivate);
            UpdateWindow(window);

            while (!_closing && IsWindow(window))
            {
                while (PeekMessageW(out var message, 0, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                Thread.Sleep(10);
            }

            if (IsWindow(window))
                DestroyWindow(window);

            if (titleFont != 0)
                DeleteObject(titleFont);
            if (textFont != 0)
                DeleteObject(textFont);

            _window = 0;
        }
        catch
        {
            _window = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitCommonControlsEx
    {
        public uint Size;
        public uint Icc;
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

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitCommonControlsEx(ref InitCommonControlsEx init);

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
