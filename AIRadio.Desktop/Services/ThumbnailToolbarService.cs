using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using AIRadio.Desktop.Models;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using Serilog;
using SkiaSharp;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 任务栏缩略图工具栏（Thumbnail Toolbar，ITaskbarList3）：鼠标悬停任务栏图标时在
/// 窗口缩略图下方显示一排应用自定义按钮（上一首/播放暂停/下一首/心形收藏）——
/// SMTC 无自定义按钮通道（19041 契约全成员探针实证），网易云等桌面播放器悬停
/// 卡片里的"喜欢"心形走的就是此通道。按钮点击以 WM_COMMAND(THBN_CLICKED) 投递
/// 到窗口，经 GWLP_WNDPROC 子类化拦截（进程未声明 comctl32 v6 激活上下文，
/// SetWindowSubclass 不可用），回调即在 UI 线程。图标运行时 Skia 光栅化
/// （32×32 straight-alpha BGRA → CreateIconIndirect，任务栏按 DPI 缩放显示），
/// 随播放态（播放/暂停）与收藏态（实心/空心心形）经 ThumbBarUpdateButtons 切换；
/// tooltip 走 AppLanguage.T 并随语言切换刷新。挂载失败静默降级不阻断启动
/// （同 SmtcService 语义）。
/// </summary>
internal sealed class ThumbnailToolbarService : IDisposable
{
    internal const int ButtonIdPrevious = 1;
    internal const int ButtonIdPlayPause = 2;
    internal const int ButtonIdNext = 3;
    internal const int ButtonIdFavorite = 4;

    internal const uint WmCommand = 0x0111;
    internal const uint ThbnClicked = 0x1800;

    private const uint ThbIcon = 0x2;
    private const uint ThbTooltip = 0x4;
    private const int GWlpWndProc = -4;

    private readonly IAudioService _audio;
    private readonly CompositeDisposable _subscriptions = new();
    // prev, play, pause, next, heartFilled, heartOutline
    private readonly IntPtr[] _icons = new IntPtr[6];
    private ITaskbarList3? _taskbarList;
    private IntPtr _hwnd;
    private IntPtr _previousWndProc;
    private WndProcDelegate? _wndProc; // 字段保活：子类过程委托不能被 GC 回收
    private bool _isPlaying;
    private bool _isFavorite;
    private bool _buttonsCreated;
    private bool _disposed;

    public ThumbnailToolbarService(IAudioService audio) => _audio = audio;

    /// <summary>心形"喜欢"按钮点击时外发；由组合根接到收藏命令（回调在 UI 线程）。</summary>
    internal event Action? FavoritePressed;

    /// <summary>窗口 Show（任务栏按钮已存在）后在 UI 线程调用；失败仅记 Debug 日志并清理半挂载。</summary>
    public bool TryAttach(Window window)
    {
        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return false;

            var taskbar = (ITaskbarList3)new TaskbarListComObject();
            taskbar.HrInit();

            for (var i = 0; i < _icons.Length; i++)
                _icons[i] = CreateTransportIcon((TransportGlyph)i);

            _wndProc = WndProc;
            _previousWndProc = SetWindowLongPtr(hwnd, GWlpWndProc,
                Marshal.GetFunctionPointerForDelegate(_wndProc));
            if (_previousWndProc == IntPtr.Zero)
                throw new InvalidOperationException("SetWindowLongPtr failed");

            _hwnd = hwnd;
            _taskbarList = taskbar;
            _isPlaying = _audio.IsPlaying;

            // ThumbBarAddButtons 每窗口仅允许一次，后续一切变更（状态/语言）走 UpdateButtons
            var initialButtons = BuildButtons(_isPlaying, _isFavorite, _icons);
            var hr = taskbar.ThumbBarAddButtons(hwnd, (uint)initialButtons.Length, initialButtons);
            if (hr != 0)
            {
                Log.Debug("ThumbBarAddButtons failed hr=0x{Hr:X8}", hr);
                Detach();
                return false;
            }
            _buttonsCreated = true;

            // StateChanged 由 LibVLC 回调线程直发，而 TaskbarList 是 Apartment 线程模型
            // （非 agile），回 UI 线程再触碰接口指针，对齐 SmtcService 的线程防护精神
            _subscriptions.Add(_audio.StateChanged
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(OnStateChanged));
            // 语言切换刷新 tooltip：状态变化之外的唯一独立触发源
            _subscriptions.Add(Disposable.Create(() => AppLanguage.Changed -= OnLanguageChanged));
            AppLanguage.Changed += OnLanguageChanged;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Thumbnail toolbar attach failed; taskbar hover buttons stay disabled");
            Detach();
            return false;
        }
    }

    /// <summary>internal 供测试直测：仅当 WM_COMMAND 且通知码为 THBN_CLICKED 且 id 属于本工具栏时返回按钮 id。</summary>
    internal static int? MatchThumbCommand(uint message, IntPtr wParam)
    {
        if (message != WmCommand)
            return null;
        var value = (ulong)wParam.ToInt64();
        var id = (int)(value & 0xFFFF);
        var notification = (uint)((value >> 16) & 0xFFFF);
        return notification == ThbnClicked && id >= ButtonIdPrevious && id <= ButtonIdFavorite
            ? id
            : null;
    }

    /// <summary>internal 供测试直驱；Play/Pause 幂等（AudioService 在目标态早退），条件分发只为与图标状态一致。</summary>
    internal bool HandleCommand(int buttonId)
    {
        switch (buttonId)
        {
            case ButtonIdPrevious:
                _audio.Previous();
                return true;
            case ButtonIdPlayPause:
                if (_audio.IsPlaying)
                    _audio.Pause();
                else
                    _audio.Play();
                return true;
            case ButtonIdNext:
                _audio.Next();
                return true;
            case ButtonIdFavorite:
                FavoritePressed?.Invoke();
                return true;
            default:
                return false;
        }
    }

    /// <summary>由组合根在当前曲目收藏态变化时推送；未挂载只记状态（挂载时生效）。</summary>
    internal void SetFavoriteState(bool liked)
    {
        _isFavorite = liked;
        UpdateButtons();
    }

    /// <summary>internal 供测试直测：按钮布局是纯函数——id 顺序、图标按状态取用、tooltip 按语言现算。</summary>
    internal static THUMBBUTTON[] BuildButtons(bool isPlaying, bool isFavorite, IntPtr[] icons) => new[]
    {
        new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip,
            iId = ButtonIdPrevious,
            hIcon = icons[0],
            szTip = AppLanguage.T("上一首", "Previous"),
        },
        new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip,
            iId = ButtonIdPlayPause,
            hIcon = isPlaying ? icons[2] : icons[1], // 播放中亮"暂停"，暂停亮"播放"
            szTip = AppLanguage.T("播放/暂停", "Play/Pause"),
        },
        new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip,
            iId = ButtonIdNext,
            hIcon = icons[3],
            szTip = AppLanguage.T("下一首", "Next"),
        },
        new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip,
            iId = ButtonIdFavorite,
            hIcon = isFavorite ? icons[4] : icons[5], // 已收藏实心，未收藏空心
            szTip = AppLanguage.T("收藏（同时记入长期口味）", "Favorite (feeds your taste profile)"),
        },
    };

    private void OnStateChanged(PlaybackState state)
    {
        _isPlaying = state == PlaybackState.Playing;
        UpdateButtons();
    }

    private void OnLanguageChanged() => UpdateButtons();

    private void UpdateButtons()
    {
        var taskbar = _taskbarList;
        if (taskbar == null || !_buttonsCreated)
            return;
        try
        {
            var hr = taskbar.ThumbBarUpdateButtons(_hwnd, 4, BuildButtons(_isPlaying, _isFavorite, _icons));
            if (hr != 0)
                Log.Debug("ThumbBarUpdateButtons failed hr=0x{Hr:X8}", hr);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ThumbBarUpdateButtons threw");
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (TryHandleMessage(message, wParam))
            return IntPtr.Zero;
        return CallWindowProc(_previousWndProc, hWnd, message, wParam, lParam);
    }

    /// <summary>internal 供测试直驱：拦截匹配的缩略图按钮点击并兜住命令异常，防止托管异常穿透原生 WndProc。</summary>
    internal bool TryHandleMessage(uint message, IntPtr wParam)
    {
        var buttonId = MatchThumbCommand(message, wParam);
        if (buttonId is not > 0)
            return false;
        try
        {
            _ = HandleCommand(buttonId.Value);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Thumbnail toolbar command dispatch failed for button {ButtonId}", buttonId.Value);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
    }

    private void Detach()
    {
        _subscriptions.Dispose();
        if (_hwnd != IntPtr.Zero && _previousWndProc != IntPtr.Zero)
        {
            // 还原子类化；窗口已销毁时失败无害（进程退出路径）
            SetWindowLongPtr(_hwnd, GWlpWndProc, _previousWndProc);
            _previousWndProc = IntPtr.Zero;
        }
        _hwnd = IntPtr.Zero;
        _taskbarList = null;
        _buttonsCreated = false;
        _wndProc = null;
        for (var i = 0; i < _icons.Length; i++)
        {
            if (_icons[i] != IntPtr.Zero)
            {
                _ = DestroyIcon(_icons[i]);
                _icons[i] = IntPtr.Zero;
            }
        }
    }

    internal enum TransportGlyph { Previous, Play, Pause, Next, HeartFilled, HeartOutline }

    private static IntPtr CreateTransportIcon(TransportGlyph glyph)
        => CreateIconFromPixels(RenderGlyphPixels(glyph));

    /// <summary>internal 供测试直测：按字形光栅化（绘制失效/字形混同在此层暴露）。</summary>
    internal static byte[] RenderGlyphPixels(TransportGlyph glyph)
        => RenderPixels(canvas => DrawGlyph(canvas, glyph));

    /// <summary>internal 供测试直测：光栅化 32×32，straight-alpha BGRA（CreateIconIndirect 期望非预乘）。</summary>
    internal static byte[] RenderPixels(Action<SKCanvas> draw)
    {
        const int size = 32;
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        draw(surface.Canvas);
        using var pixmap = surface.PeekPixels();
        var pixels = new byte[size * size * 4];
        Marshal.Copy(pixmap.GetPixels(), pixels, 0, pixels.Length);
        return Unpremultiply(pixels);
    }

    /// <summary>Skia 输出预乘像素而 Windows 32bpp 图标 alpha 为非预乘，逐像素还原（边缘抗锯齿才不偏暗）。</summary>
    private static byte[] Unpremultiply(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a is 0 or 255)
                continue;
            bgra[i] = ScaleToAlpha(bgra[i], a);
            bgra[i + 1] = ScaleToAlpha(bgra[i + 1], a);
            bgra[i + 2] = ScaleToAlpha(bgra[i + 2], a);
        }
        return bgra;

        static byte ScaleToAlpha(byte color, byte alpha)
            => (byte)Math.Min(255, color * 255 / alpha);
    }

    /// <summary>internal 供测试直测：纯 GDI 图标创建不依赖窗口，headless 可跑。</summary>
    internal static IntPtr CreateIconFromPixels(byte[] bgra32)
    {
        const int size = 32;
        var color = CreateBitmap(size, size, 1, 32, bgra32);
        // 全零 AND 掩码：32bpp 走 color 位图的 alpha 通道
        var mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info); // 复制位图，原位图可立即释放
        }
        finally
        {
            _ = DeleteObject(color);
            _ = DeleteObject(mask);
        }
    }

    private static void DrawGlyph(SKCanvas canvas, TransportGlyph glyph)
    {
        using var fill = new SKPaint
        {
            Color = new SKColor(0xED, 0xED, 0xF5), // C_FFEDEDF5，与应用文字色一致（thumbbar 深灰底）
            IsAntialias = true,
        };
        switch (glyph)
        {
            case TransportGlyph.Previous:
                canvas.DrawRect(6, 8, 3, 16, fill);
                DrawTriangle(canvas, fill, 24, 8, 24, 24, 12, 16);
                break;
            case TransportGlyph.Play:
                DrawTriangle(canvas, fill, 11, 8, 11, 24, 25, 16);
                break;
            case TransportGlyph.Pause:
                canvas.DrawRect(10, 8, 4, 16, fill);
                canvas.DrawRect(18, 8, 4, 16, fill);
                break;
            case TransportGlyph.Next:
                DrawTriangle(canvas, fill, 8, 8, 8, 24, 20, 16);
                canvas.DrawRect(23, 8, 3, 16, fill);
                break;
            case TransportGlyph.HeartFilled:
                using (var heart = BuildHeartPath())
                    canvas.DrawPath(heart, fill);
                break;
            case TransportGlyph.HeartOutline:
                using (var heart = BuildHeartPath())
                using (var stroke = new SKPaint
                {
                    Color = fill.Color,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.5f,
                    StrokeJoin = SKStrokeJoin.Round,
                })
                {
                    canvas.DrawPath(heart, stroke);
                }
                break;
        }
    }

    private static void DrawTriangle(SKCanvas canvas, SKPaint paint,
        float x1, float y1, float x2, float y2, float x3, float y3)
    {
        using var path = new SKPath();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        path.LineTo(x3, y3);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>经典参数心形 x=16sin³t、y=13cost−5cos2t−2cos3t−cos4t，折线化映射到 32×32 中心。</summary>
    private static SKPath BuildHeartPath()
    {
        const int steps = 64;
        var path = new SKPath();
        for (var i = 0; i <= steps; i++)
        {
            var t = 2 * Math.PI * i / steps;
            var x = 16 + 0.72 * 16 * Math.Pow(Math.Sin(t), 3);
            var y = 16 - 0.72 * (13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t));
            if (i == 0)
                path.MoveTo((float)x, (float)y);
            else
                path.LineTo((float)x, (float)y);
        }
        path.Close();
        return path;
    }

    // 字段集对照 SDK shobjidl_core.h（dwMask/iId/iBitmap/hIcon/szTip/dwFlags，无 szTipDescription——
    // pinvoke.net 老模板里的该字段不存在于真实 ABI）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [ComImport]
    [Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    internal class TaskbarListComObject
    {
    }

    /// <summary>vtable 顺序（ITaskbarList→ITaskbarList2→ITaskbarList3）必须逐字对齐，声明截断到本服务用到的 ThumbBarUpdateButtons。internal 供真实激活冒烟锚定 COM 通道。</summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr insertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint reserved);
        [PreserveSig]
        int ThumbBarAddButtons(IntPtr hwnd, uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
        // 槽位顺序对照 SDK shobjidl_core.h：AddButtons(12) → UpdateButtons(13) → SetImageList(14)，
        // 写反会让 UpdateButtons 实际执行 SetImageList（初始注册恰好同槽故单测不可见）
        [PreserveSig]
        int ThumbBarUpdateButtons(IntPtr hwnd, uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel,
        byte[] lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
}
