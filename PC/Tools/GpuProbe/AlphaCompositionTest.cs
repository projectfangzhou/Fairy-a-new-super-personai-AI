using System.Runtime.InteropServices;

namespace GpuProbe;

/// <summary>
/// Answers the question that decides whether a GPU surface can carry the orb, the dynamic
/// island or a transparent Live2D window: which of the four Windows composition routes can
/// actually blend a surface with what is behind it?
///
/// A green backdrop is painted first, then each route covers it and the composited pixel is
/// read straight off the desktop DC. Every route ends with a control (opaque red) whose
/// absence would make any "alpha didn't work" verdict meaningless.
/// </summary>
internal static class AlphaCompositionTest
{
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;

    private const int BackdropColor = 0x0000FF00; // 0x00BBGGRR: pure green
    private const int LWA_ALPHA = 0x00000002;
    private const int CS_OWNDC = 0x0020;
    private const int BI_RGB = 0;
    private const int AC_SRC_OVER = 0;
    private const int AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 0x00000002;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref Win32.WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, IntPtr paintStruct);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hWnd, IntPtr paintStruct);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int colorKey, byte alpha, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Win32.MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Win32.MSG msg);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    private static readonly IntPtr HwndTopmost = new(-1);
    private static WndProc? _paintProc;

    internal sealed record Pixel(int Red, int Green, int Blue)
    {
        public override string ToString() => $"R={Red} G={Green} B={Blue}";
    }

    internal sealed record Step(string Name, Pixel? Observed, string Interpretation);

    internal sealed record Result(bool Valid, Step[] Steps, string Conclusion, string? Failure);

    internal static Result Run(int x, int y, int size)
    {
        var greenBrush = CreateSolidBrush(BackdropColor);
        var windows = new List<IntPtr>();
        var centerX = x + size / 2;
        var centerY = y + size / 2;

        try
        {
            var className = "GpuProbeSurface_" + Guid.NewGuid().ToString("N");
            _paintProc = (hWnd, msg, wParam, lParam) =>
            {
                switch (msg)
                {
                    case WM_ERASEBKGND:
                        return new IntPtr(1);
                    case WM_PAINT:
                    {
                        var ps = Marshal.AllocHGlobal(128);
                        try
                        {
                            var hdc = BeginPaint(hWnd, ps);
                            if (hWnd == windows.FirstOrDefault())
                            {
                                var rect = new RECT { left = 0, top = 0, right = size, bottom = size };
                                FillRect(hdc, ref rect, greenBrush);
                            }
                            EndPaint(hWnd, ps);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(ps);
                        }
                        return IntPtr.Zero;
                    }
                    default:
                        return DefWindowProc(hWnd, msg, wParam, lParam);
                }
            };

            if (!RegisterClass(className, _paintProc))
                return Invalid($"注册窗口类失败（错误 {Marshal.GetLastWin32Error()}）");

            // 0. Backdrop: proves the desktop is readable and the sampling point is ours.
            var backdrop = CreateTopmostWindow(className, 0, x, y, size);
            if (backdrop == IntPtr.Zero) return Invalid($"创建背景窗口失败（错误 {Marshal.GetLastWin32Error()}）");
            windows.Add(backdrop);
            Settle();
            var backdropPixel = ReadDesktop(centerX, centerY);
            if (backdropPixel is null) return Invalid("读不到桌面像素：当前会话没有可读的交互桌面");
            if (backdropPixel.Green < 180 || backdropPixel.Red > 80)
            {
                return new Result(false,
                    new[] { new Step("背景窗口", backdropPixel, "背景没有出现在屏幕上，后续结论不可信") },
                    "未取得有效结论",
                    "背景窗口可能被全屏窗口覆盖，或当前桌面不可见");
            }

            var steps = new List<Step> { new("背景窗口（预期绿色）", backdropPixel, "桌面可读，采样点位于测试区域") };

            // 1. Plain (non-layered) window filled by OpenGL: does the swapchain appear at all?
            var plain = CreateTopmostWindow(className, Win32.WS_EX_TOOLWINDOW, x, y, size);
            if (plain == IntPtr.Zero) return Invalid($"创建 GPU 表面窗口失败（错误 {Marshal.GetLastWin32Error()}）");
            windows.Add(plain);
            SetWindowPos(plain, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

            var glOpaque = GlProbe.RunLayered(plain, 1.0f, 0.0f, 0.0f, 1.0f);
            if (!glOpaque.Available) return Invalid(glOpaque.Failure ?? "OpenGL 上下文不可用");
            Settle();
            var opaquePixel = ReadDesktop(centerX, centerY);
            steps.Add(new Step("普通窗口 + OpenGL 不透明红（对照）", opaquePixel,
                opaquePixel is not null && opaquePixel.Red > 150 && opaquePixel.Green < 100
                    ? "GPU 内容确实上屏"
                    : "GPU 内容没有上屏，后续 alpha 结论不成立"));

            if (opaquePixel is null || opaquePixel.Red < 150 || opaquePixel.Green > 100)
            {
                DestroyWindow(plain);
                return new Result(false, steps.ToArray(), "未取得有效结论",
                    $"OpenGL 报告 {glOpaque.Renderer}，但不透明填充也没出现在屏幕上");
            }

            // 2. Same window, same swapchain, 50% alpha in the framebuffer.
            var glAlpha = GlProbe.RunLayered(plain, 0.5f, 0.0f, 0.0f, 0.5f);
            if (!glAlpha.Available) return Invalid(glAlpha.Failure ?? "OpenGL 上下文不可用");
            Settle();
            var alphaPixel = ReadDesktop(centerX, centerY);
            steps.Add(new Step("普通窗口 + OpenGL 50% alpha", alphaPixel,
                alphaPixel is not null && alphaPixel.Green > 60
                    ? "帧缓冲 alpha 参与了桌面合成"
                    : "帧缓冲 alpha 被忽略，交换链按不透明呈现"));
            DestroyWindow(plain);
            windows.Remove(plain);
            Settle();

            // 3. Layered window with a single constant alpha for the whole window.
            var constant = CreateTopmostWindow(className, Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW, x, y, size);
            if (constant == IntPtr.Zero) return Invalid($"创建统一 alpha 窗口失败（错误 {Marshal.GetLastWin32Error()}）");
            SetWindowPos(constant, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            if (!SetLayeredWindowAttributes(constant, 0, 128, LWA_ALPHA))
                steps.Add(new Step("分层窗口 + SetLayeredWindowAttributes(128)", null,
                    $"调用失败（错误 {Marshal.GetLastWin32Error()}）"));
            else
            {
                var glConstant = GlProbe.RunLayered(constant, 1.0f, 0.0f, 0.0f, 1.0f);
                Settle();
                var constantPixel = ReadDesktop(centerX, centerY);
                steps.Add(new Step("分层窗口 + 整窗 alpha=128 + OpenGL 不透明红", constantPixel,
                    constantPixel is not null && constantPixel.Green > 60
                        ? "整窗统一 alpha 有效（但不能逐像素）"
                        : "整窗统一 alpha 未生效"));
                if (!glConstant.Available)
                    steps.Add(new Step("分层窗口 + OpenGL", null, glConstant.Failure ?? "OpenGL 上下文不可用"));
            }
            DestroyWindow(constant);
            windows.Remove(constant);
            Settle();

            // 4. The per-pixel route Windows actually supports: pixels supplied by the CPU.
            var perPixelPixel = RunUpdateLayeredWindow(className, x, y, size, centerX, centerY);
            steps.Add(new Step("分层窗口 + UpdateLayeredWindow（CPU 预乘像素，50% 红）", perPixelPixel,
                perPixelPixel is not null && perPixelPixel.Green > 60
                    ? "逐像素 alpha 可行，但像素来自 CPU 位图，不走 GPU 交换链"
                    : "UpdateLayeredWindow 未产生混合结果"));

            var conclusion = BuildConclusion(steps);
            return new Result(true, steps.ToArray(), conclusion, null);
        }
        finally
        {
            foreach (var window in windows)
                if (window != IntPtr.Zero) DestroyWindow(window);
            DeleteObject(greenBrush);
        }
    }

    private static Pixel? RunUpdateLayeredWindow(string className, int x, int y, int size, int centerX, int centerY)
    {
        var window = CreateTopmostWindow(className, Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW, x, y, size);
        if (window == IntPtr.Zero) return null;

        var screenDc = Win32.GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        IntPtr dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };

            dib = CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = SelectObject(memoryDc, dib);

            // Premultiplied 50% red: B=0 G=0 R=128 A=128.
            for (var i = 0; i < size * size; i++)
            {
                Marshal.WriteByte(bits, i * 4 + 0, 0);
                Marshal.WriteByte(bits, i * 4 + 1, 0);
                Marshal.WriteByte(bits, i * 4 + 2, 128);
                Marshal.WriteByte(bits, i * 4 + 3, 128);
            }

            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            var destination = new POINT { x = x, y = y };
            var source = new POINT { x = 0, y = 0 };
            var dimensions = new SIZE { cx = size, cy = size };

            if (!UpdateLayeredWindow(window, screenDc, ref destination, ref dimensions, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
                return null;

            Settle();
            return ReadDesktop(centerX, centerY);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memoryDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
            DestroyWindow(window);
        }
    }

    private static string BuildConclusion(List<Step> steps)
    {
        var gpuAlpha = steps.FirstOrDefault(s => s.Name.Contains("50% alpha"));
        var constant = steps.FirstOrDefault(s => s.Name.Contains("整窗 alpha"));
        var perPixel = steps.FirstOrDefault(s => s.Name.Contains("UpdateLayeredWindow"));

        var parts = new List<string>();
        if (gpuAlpha?.Interpretation.Contains("忽略") == true)
            parts.Add("GPU 交换链的 alpha 不参与桌面合成");
        else if (gpuAlpha?.Interpretation.Contains("参与") == true)
            parts.Add("GPU 交换链的 alpha 参与了桌面合成");

        if (constant?.Interpretation.Contains("有效") == true)
            parts.Add("分层窗口可以做整窗统一 alpha");
        if (perPixel?.Interpretation.Contains("可行") == true)
            parts.Add("逐像素 alpha 只能由 CPU 提供像素（UpdateLayeredWindow）");

        return parts.Count == 0 ? "没有得到可用的合成结论" : string.Join("；", parts);
    }

    private static IntPtr CreateTopmostWindow(string className, int exStyle, int x, int y, int size)
    {
        var hwnd = CreateWindowEx(exStyle, className, "gpuprobe-surface", Win32.WS_POPUP | Win32.WS_VISIBLE,
            x, y, size, size, IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        return hwnd;
    }

    private static bool RegisterClass(string name, WndProc proc)
    {
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = Win32.GetModuleHandle(null),
            lpszClassName = name,
        };
        return RegisterClassEx(ref wc) != 0;
    }

    private static Pixel? ReadDesktop(int x, int y)
    {
        var hdc = Win32.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            var value = Win32.GetPixel(hdc, x, y);
            if (value == 0xFFFFFFFF) return null;
            var packed = (int)value & 0x00FFFFFF;
            return new Pixel(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF);
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// Messages must be dispatched, not merely removed: a discarded WM_PAINT leaves the window
    /// invalid forever and the queue never drains.
    /// </summary>
    private static void Pump()
    {
        var guard = 0;
        while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            if (++guard <= 20000) continue;
            Log("  警告: 消息队列持续非空（超过 20000 条），停止泵消息");
            break;
        }
    }

    private static void Settle()
    {
        Pump();
        Thread.Sleep(180);
        Pump();
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
    }

    private static Result Invalid(string message) =>
        new(false, Array.Empty<Step>(), "未取得有效结论", message);
}
