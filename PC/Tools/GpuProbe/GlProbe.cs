using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GpuProbe;

internal sealed record GlReport(
    bool Available,
    string? Version,
    string? Renderer,
    string? Vendor,
    int AlphaBits,
    int ColorBits,
    int DepthBits,
    bool DoubleBuffered,
    bool HasSwapControlExtension,
    double PresentMsPerFrame,
    int PresentFrames,
    string? Failure);

internal static class GlProbe
{
    private const int PFD_DOUBLEBUFFER = 0x00000001;
    private const int PFD_DRAW_TO_WINDOW = 0x00000004;
    private const int PFD_SUPPORT_OPENGL = 0x00000020;
    private const int PFD_TYPE_RGBA = 0;

    private const int GL_VENDOR = 0x1F00;
    private const int GL_RENDERER = 0x1F01;
    private const int GL_VERSION = 0x1F02;
    private const int GL_ALPHA_BITS = 0x0D55;
    private const int GL_COLOR_BUFFER_BIT = 0x00004000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public int dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public int dwLayerMask;
        public int dwVisibleMask;
        public int dwDamageMask;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int DescribePixelFormat(IntPtr hdc, int format, uint size, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwapBuffers(IntPtr hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    [DllImport("opengl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglDeleteContext(IntPtr hglrc);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll")]
    private static extern IntPtr glGetString(int name);

    [DllImport("opengl32.dll")]
    private static extern void glGetIntegerv(int pname, out int data);

    [DllImport("opengl32.dll")]
    private static extern void glClearColor(float r, float g, float b, float a);

    [DllImport("opengl32.dll")]
    private static extern void glClear(int mask);

    [DllImport("opengl32.dll", EntryPoint = "glFinish")]
    private static extern void glFinish();

    private sealed record ContextSetup(IntPtr Context, PIXELFORMATDESCRIPTOR Format, string? Failure);

    private static ContextSetup CreateContext(IntPtr hdc)
    {
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType = PFD_TYPE_RGBA,
            cColorBits = 32,
            cAlphaBits = 8,
            cDepthBits = 24,
        };

        var format = ChoosePixelFormat(hdc, ref pfd);
        if (format == 0)
            return new ContextSetup(IntPtr.Zero, pfd, $"ChoosePixelFormat 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

        if (!SetPixelFormat(hdc, format, ref pfd))
            return new ContextSetup(IntPtr.Zero, pfd, $"SetPixelFormat 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

        DescribePixelFormat(hdc, format, (uint)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), ref pfd);

        var context = wglCreateContext(hdc);
        if (context == IntPtr.Zero)
            return new ContextSetup(IntPtr.Zero, pfd, $"wglCreateContext 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

        if (!wglMakeCurrent(hdc, context))
            return new ContextSetup(IntPtr.Zero, pfd, $"wglMakeCurrent 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

        return new ContextSetup(context, pfd, null);
    }

    internal static GlReport Run(IntPtr hwnd, int frames)
    {
        var hdc = Win32.GetDC(hwnd);
        if (hdc == IntPtr.Zero)
            return Failure("GetDC 失败");

        IntPtr hglrc = IntPtr.Zero;
        try
        {
            var setup = CreateContext(hdc);
            if (setup.Failure is not null) return Failure(setup.Failure);
            hglrc = setup.Context;

            var version = ReadString(GL_VERSION);
            var renderer = ReadString(GL_RENDERER);
            var vendor = ReadString(GL_VENDOR);
            glGetIntegerv(GL_ALPHA_BITS, out var alphaBits);

            var swapControl = wglGetProcAddress("wglSwapIntervalEXT") != IntPtr.Zero;

            glClearColor(0.15f, 0.10f, 0.30f, 1.0f);
            glClear(GL_COLOR_BUFFER_BIT);
            SwapBuffers(hdc);

            var measured = 0;
            var elapsed = TimeSpan.Zero;
            if (frames > 0)
            {
                var stopwatch = Stopwatch.StartNew();
                for (var i = 0; i < frames; i++)
                {
                    // Alternate the colour so the driver cannot collapse identical frames.
                    glClearColor(0.15f, 0.10f, 0.30f, (i % 2 == 0) ? 0.5f : 0.49f);
                    glClear(GL_COLOR_BUFFER_BIT);
                    SwapBuffers(hdc);
                    glFinish();
                    measured++;
                }
                stopwatch.Stop();
                elapsed = stopwatch.Elapsed;
            }

            var perFrame = measured == 0 ? 0 : elapsed.TotalMilliseconds / measured;

            return new GlReport(true, version, renderer, vendor, alphaBits,
                setup.Format.cColorBits, setup.Format.cDepthBits, (setup.Format.dwFlags & PFD_DOUBLEBUFFER) != 0,
                swapControl, perFrame, measured, null);
        }
        finally
        {
            if (hglrc != IntPtr.Zero)
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(hglrc);
            }
            Win32.ReleaseDC(hwnd, hdc);
        }
    }

    /// <summary>
    /// Fills a window with a translucent colour through the GPU path, then swaps a few
    /// frames so the compositor has something to blend. Used by the alpha composition test.
    /// </summary>
    internal static GlReport RunLayered(IntPtr hwnd, float red, float green, float blue, float alpha, int frames = 40)
    {
        var hdc = Win32.GetDC(hwnd);
        if (hdc == IntPtr.Zero)
            return Failure("GetDC 失败");

        IntPtr hglrc = IntPtr.Zero;
        try
        {
            var setup = CreateContext(hdc);
            if (setup.Failure is not null) return Failure(setup.Failure);
            hglrc = setup.Context;

            glClearColor(red, green, blue, alpha);
            for (var i = 0; i < frames; i++)
            {
                glClear(GL_COLOR_BUFFER_BIT);
                SwapBuffers(hdc);
                glFinish();
            }

            return new GlReport(true, ReadString(GL_VERSION), ReadString(GL_RENDERER), ReadString(GL_VENDOR),
                0, setup.Format.cColorBits, 0, true, false, 0, frames, null);
        }
        finally
        {
            if (hglrc != IntPtr.Zero)
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(hglrc);
            }
            Win32.ReleaseDC(hwnd, hdc);
        }
    }

    private static string? ReadString(int name)
    {
        var pointer = glGetString(name);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
    }

    private static GlReport Failure(string message) =>
        new(false, null, null, null, 0, 0, 0, false, false, 0, 0, message);
}
