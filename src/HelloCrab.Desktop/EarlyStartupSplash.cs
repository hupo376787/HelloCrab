using System.Runtime.InteropServices;

namespace HelloCrab.Desktop;

/// <summary>
/// Windows 冷启动阶段使用的单一原生启动闪屏。
/// 仅负责在 Avalonia 初始化前立即给用户视觉反馈；启动时序由 Program/App 管理。
/// </summary>
internal sealed class EarlyStartupSplash : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipChildren = 0x02000000;

    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 0x0001;
    private const uint WmPaint = 0x000F;
    private const uint WmClose = 0x0010;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmAppRefresh = 0x8001;

    private const int TransparentBkMode = 1;
    private const int NullPenStockObject = 8;

    private const uint DtLeft = 0x0000;
    private const uint DtCenter = 0x0001;
    private const uint DtRight = 0x0002;
    private const uint DtVCenter = 0x0004;
    private const uint DtSingleLine = 0x0020;
    private const uint DtNoPrefix = 0x0800;
    private const uint DtEndEllipsis = 0x8000;

    private const uint DiNormal = 0x0003;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private const int WindowWidth = 560;
    private const int WindowHeight = 330;

    private const int ContentLeft = 36;
    private const int ContentRight = 524;
    private const int ContentWidth = ContentRight - ContentLeft;

    // 启动状态区整体靠近窗口底部，顶部品牌区保持不动。
    private const int StatusTop = 205;
    private const int StatusBottom = 229;
    private const int ProgressTop = 241;
    private const int ProgressHeight = 10;
    private const int DetailTop = 263;
    private const int DetailBottom = 291;

    private static readonly WndProcDelegate WindowProcDelegate = WindowProc;
    private static EarlyStartupSplash? _activeInstance;

    private readonly Thread _thread;
    private readonly string _windowClassName;
    private readonly ManualResetEventSlim _ready = new(false);

    private volatile bool _closing;
    private volatile int _progressValue;
    private string _status = "正在启动 HelloCrab…";
    private string _detail = "准备应用运行环境";

    private nint _window;
    private nint _backgroundBrush;
    private nint _logoTileBrush;
    private nint _progressTrackBrush;
    private nint _progressFillBrush;
    private nint _dividerBrush;
    private nint _borderPen;

    private nint _titleFont;
    private nint _subtitleFont;
    private nint _statusFont;
    private nint _detailFont;
    private nint _percentFont;

    private nint _appIcon;

    private EarlyStartupSplash()
    {
        _windowClassName = $"HelloCrab.StartupSplash.{Environment.ProcessId}";
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "HelloCrab Startup Splash"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等原生窗口真正创建成功再继续初始化 Avalonia；如果创建失败则回退到 Avalonia 闪屏。
        if (!_ready.Wait(TimeSpan.FromMilliseconds(500)) || _window == 0)
            throw new InvalidOperationException("Unable to create native startup splash.");
    }

    public DateTimeOffset ShownAt { get; private set; }

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
        _progressValue = (int)Math.Round(Math.Clamp(percent, 0d, 100d));
        _status = string.IsNullOrWhiteSpace(status) ? "正在启动 HelloCrab…" : status;
        _detail = detail ?? string.Empty;

        var window = _window;
        if (window != 0)
            PostMessageW(window, WmAppRefresh, 0, 0);
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
        ushort classAtom = 0;

        try
        {
            var instance = GetModuleHandleW(null);
            CreateVisualResources();

            _activeInstance = this;

            var windowClass = new WindowClassExData
            {
                Size = (uint)Marshal.SizeOf<WindowClassExData>(),
                Instance = instance,
                BackgroundBrush = _backgroundBrush,
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
                28,
                28);
            if (roundedRegion != 0)
                _ = SetWindowRgn(window, roundedRegion, true);

            ShowWindow(window, SwShowNoActivate);
            InvalidateRect(window, 0, false);
            UpdateWindow(window);

            ShownAt = DateTimeOffset.UtcNow;
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

            if (ReferenceEquals(_activeInstance, this))
                _activeInstance = null;

            DestroyVisualResources();

            if (classAtom != 0)
                UnregisterClassW(_windowClassName, GetModuleHandleW(null));
        }
    }

    private void CreateVisualResources()
    {
        _backgroundBrush = CreateSolidBrush(ToColorRef(0xF9, 0xFA, 0xFE));
        _logoTileBrush = CreateSolidBrush(ToColorRef(0xF1, 0xED, 0xFF));
        _progressTrackBrush = CreateSolidBrush(ToColorRef(0xE6, 0xE8, 0xF0));
        _progressFillBrush = CreateSolidBrush(ToColorRef(0x7C, 0x3A, 0xED));
        _dividerBrush = CreateSolidBrush(ToColorRef(0xED, 0xEE, 0xF4));
        _borderPen = CreatePen(0, 1, ToColorRef(0xDC, 0xD4, 0xF7));

        _titleFont = CreateFontW(
            -31, 0, 0, 0, 600,
            0, 0, 0, 1, 0, 0, 5, 0,
            "Segoe UI");
        _subtitleFont = CreateFontW(
            -15, 0, 0, 0, 400,
            0, 0, 0, 1, 0, 0, 5, 0,
            "Microsoft YaHei UI");
        _statusFont = CreateFontW(
            -16, 0, 0, 0, 600,
            0, 0, 0, 1, 0, 0, 5, 0,
            "Microsoft YaHei UI");
        _detailFont = CreateFontW(
            -14, 0, 0, 0, 400,
            0, 0, 0, 1, 0, 0, 5, 0,
            "Microsoft YaHei UI");
        _percentFont = CreateFontW(
            -15, 0, 0, 0, 600,
            0, 0, 0, 1, 0, 0, 5, 0,
            "Segoe UI");

        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            _ = ExtractIconExW(executablePath, 0, out var largeIcon, out var smallIcon, 1);
            _appIcon = largeIcon != 0 ? largeIcon : smallIcon;

            if (smallIcon != 0 && smallIcon != _appIcon)
                DestroyIcon(smallIcon);
        }
    }

    private void DestroyVisualResources()
    {
        DeleteGdiObject(ref _titleFont);
        DeleteGdiObject(ref _subtitleFont);
        DeleteGdiObject(ref _statusFont);
        DeleteGdiObject(ref _detailFont);
        DeleteGdiObject(ref _percentFont);

        DeleteGdiObject(ref _backgroundBrush);
        DeleteGdiObject(ref _logoTileBrush);
        DeleteGdiObject(ref _progressTrackBrush);
        DeleteGdiObject(ref _progressFillBrush);
        DeleteGdiObject(ref _dividerBrush);
        DeleteGdiObject(ref _borderPen);

        if (_appIcon != 0)
        {
            DestroyIcon(_appIcon);
            _appIcon = 0;
        }
    }

    private static void DeleteGdiObject(ref nint handle)
    {
        if (handle == 0)
            return;

        DeleteObject(handle);
        handle = 0;
    }

    private void PaintWindow(nint hwnd)
    {
        var hdc = GetDC(hwnd);
        if (hdc == 0)
            return;

        try
        {
            var clientRect = new Rect(0, 0, WindowWidth, WindowHeight);
            FillRect(hdc, ref clientRect, _backgroundBrush);

            // 外框：轻微紫灰边界，与主界面紫色强调色保持一致。
            var oldPen = SelectObject(hdc, _borderPen);
            var oldBrush = SelectObject(hdc, GetStockObject(5));
            RoundRect(hdc, 0, 0, WindowWidth - 1, WindowHeight - 1, 28, 28);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);

            // Logo 背板 + 应用自身图标。
            FillRoundRect(hdc, _logoTileBrush, 36, 32, 96, 92, 18);
            if (_appIcon != 0)
                DrawIconEx(hdc, 43, 39, _appIcon, 46, 46, 0, 0, DiNormal);
            else
                DrawFallbackLogo(hdc);

            DrawText(
                hdc,
                "HelloCrab",
                _titleFont,
                ToColorRef(0x17, 0x20, 0x33),
                new Rect(112, 37, 515, 69),
                DtLeft | DtVCenter | DtSingleLine | DtNoPrefix);

            DrawText(
                hdc,
                "正在准备应用，请稍候",
                _subtitleFont,
                ToColorRef(0x7A, 0x84, 0x98),
                new Rect(112, 72, 515, 98),
                DtLeft | DtVCenter | DtSingleLine | DtNoPrefix);

            // 内容区用一条非常淡的分隔线拉开层次。
            var dividerRect = new Rect(ContentLeft, 116, ContentRight, 117);
            FillRect(hdc, ref dividerRect, _dividerBrush);

            DrawText(
                hdc,
                _status,
                _statusFont,
                ToColorRef(0x26, 0x32, 0x4A),
                new Rect(ContentLeft, StatusTop, 454, StatusBottom),
                DtLeft | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);

            DrawText(
                hdc,
                $"{_progressValue}%",
                _percentFont,
                ToColorRef(0x7C, 0x3A, 0xED),
                new Rect(456, StatusTop, ContentRight, StatusBottom),
                DtRight | DtVCenter | DtSingleLine | DtNoPrefix);

            // 自绘胶囊进度条，避免 Win32 默认进度条的边框和生硬样式。
            FillRoundRect(
                hdc,
                _progressTrackBrush,
                ContentLeft,
                ProgressTop,
                ContentRight,
                ProgressTop + ProgressHeight,
                ProgressHeight);

            if (_progressValue > 0)
            {
                var rawFillWidth = (int)Math.Round(ContentWidth * (_progressValue / 100d));
                var fillWidth = Math.Clamp(rawFillWidth, ProgressHeight, ContentWidth);
                FillRoundRect(
                    hdc,
                    _progressFillBrush,
                    ContentLeft,
                    ProgressTop,
                    ContentLeft + fillWidth,
                    ProgressTop + ProgressHeight,
                    ProgressHeight);
            }

            DrawText(
                hdc,
                _detail,
                _detailFont,
                ToColorRef(0x7A, 0x84, 0x98),
                new Rect(ContentLeft, DetailTop, ContentRight, DetailBottom),
                DtLeft | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
    }

    private void DrawFallbackLogo(nint hdc)
    {
        DrawText(
            hdc,
            "HC",
            _statusFont,
            ToColorRef(0x7C, 0x3A, 0xED),
            new Rect(36, 32, 96, 92),
            DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
    }

    private static void DrawText(
        nint hdc,
        string text,
        nint font,
        uint color,
        Rect bounds,
        uint format)
    {
        var oldFont = SelectObject(hdc, font);
        var oldBkMode = SetBkMode(hdc, TransparentBkMode);
        var oldColor = SetTextColor(hdc, color);

        DrawTextW(hdc, text, -1, ref bounds, format);

        SetTextColor(hdc, oldColor);
        SetBkMode(hdc, oldBkMode);
        SelectObject(hdc, oldFont);
    }

    private static void FillRoundRect(
        nint hdc,
        nint brush,
        int left,
        int top,
        int right,
        int bottom,
        int radius)
    {
        var oldPen = SelectObject(hdc, GetStockObject(NullPenStockObject));
        var oldBrush = SelectObject(hdc, brush);

        RoundRect(
            hdc,
            left,
            top,
            right,
            bottom,
            radius,
            radius);

        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
    }

    private static nint WindowProc(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam)
    {
        var instance = _activeInstance;

        switch (message)
        {
            case WmEraseBkgnd:
                // 整个背景都在 WM_PAINT 中完成，跳过系统擦除可减少启动进度刷新时的闪烁。
                return 1;

            case WmAppRefresh:
                InvalidateRect(hwnd, 0, false);
                UpdateWindow(hwnd);
                return 0;

            case WmPaint when instance is not null:
                instance.PaintWindow(hwnd);
                ValidateRect(hwnd, 0);
                return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static uint ToColorRef(byte red, byte green, byte blue)
        => (uint)(red | (green << 8) | (blue << 16));

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public Rect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate nint WndProcDelegate(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(
        string file,
        int iconIndex,
        out nint largeIcon,
        out nint smallIcon,
        uint iconCount);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(nint hwnd, nint rect);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref Rect rect, nint brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(
        nint hdc,
        string text,
        int count,
        ref Rect rect,
        uint format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        nint hdc,
        int xLeft,
        int yTop,
        nint icon,
        int width,
        int height,
        uint stepIfAniCur,
        nint flickerFreeDraw,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

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

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll")]
    private static extern nint CreatePen(int style, int width, uint colorRef);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int objectIndex);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RoundRect(
        nint hdc,
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height);

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
