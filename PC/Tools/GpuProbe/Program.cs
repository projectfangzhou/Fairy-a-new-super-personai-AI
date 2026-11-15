using System.Text;
using System.Text.Json;
using MyAiAssistant.Services.Rendering;

namespace GpuProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        var frames = ParseFrames(args);
        var outDir = ParseOption(args, "--out") ?? Directory.GetCurrentDirectory();

        using var window = new Win32.OffscreenWindow(512, 512);
        var hwnd = window.Handle;

        Console.WriteLine("=== 渲染能力实测 ===");
        Console.WriteLine($"离屏窗口句柄: 0x{hwnd.ToInt64():X}（512x512，位于可见桌面之外）");
        Console.WriteLine();

        var vulkan = VulkanProbe.Run(hwnd);
        ReportVulkan(vulkan);

        var gl = GlProbe.Run(hwnd, frames);
        ReportGl(gl);

        var runAlpha = !args.Contains("--no-alpha");
        AlphaCompositionTest.Result? alpha = null;
        if (runAlpha)
        {
            Console.WriteLine("--- 逐像素透明合成实测 ---");
            Console.WriteLine("（会短暂弹出两个 160x160 的小窗口：绿色背景 + GPU 填充表面）");
            Console.Out.Flush();
            var alphaResult = AlphaCompositionTest.Run(240, 240, 160);
            alpha = alphaResult;
            foreach (var step in alphaResult.Steps)
                Console.WriteLine($"  {step.Name}: {step.Observed} — {step.Interpretation}");
            Console.WriteLine($"结论: {alphaResult.Conclusion}");
            if (alphaResult.Failure is not null) Console.WriteLine($"说明: {alphaResult.Failure}");
            Console.WriteLine();
        }

        var capabilities = new RenderCapabilities(
            vulkan.LoaderAvailable,
            vulkan.Devices.Select(d => new GpuDeviceFacts(
                d.Name, d.Type, d.ApiVersion, d.HasGraphicsQueue, d.SurfaceSupported,
                d.SupportsPremultipliedAlpha, d.HasWin32ExternalMemory)).ToArray(),
            gl.Available,
            gl.Version,
            gl.Renderer,
            DirectCompositionAvailable(),
            vulkan.Failure);

        var decision = RenderSurfacePolicy.Decide(capabilities);
        ReportDecision(decision);

        var json = JsonSerializer.Serialize(new
        {
            probedAt = DateTimeOffset.Now,
            host = new
            {
                os = Environment.OSVersion.VersionString,
                runtime = Environment.Version.ToString(),
                framesMeasured = frames,
            },
            vulkan = new
            {
                vulkan.LoaderAvailable,
                vulkan.LoaderVersion,
                vulkan.Failure,
                devices = vulkan.Devices.Select(d => new
                {
                    d.Name, d.Type, d.ApiVersion, d.DriverVersion, d.VendorId, d.DeviceId,
                    deviceLocalMemoryMb = d.DeviceLocalMemoryBytes / 1024 / 1024,
                    d.QueueFamilies, d.HasGraphicsQueue, d.SurfaceSupported,
                    d.CompositeAlpha, d.CompositeAlphaFlags, d.SurfaceFormats, d.PresentModes,
                    d.MinImageCount, d.MaxImageCount, d.SurfaceExtent,
                    d.InteropExtensions, d.HasWin32ExternalMemory, d.HasWin32ExternalSemaphore,
                    d.HasTimelineSemaphore, d.Failure,
                }),
            },
            openGl = gl,
            alphaComposition = alpha,
            directCompositionAvailable = DirectCompositionAvailable(),
            decision = new
            {
                decision.OpaqueSurfaceBackend,
                decision.TransparentSurfaceBackend,
                decision.TransparentComposition,
                decision.ChosenDevice,
                decision.CanRenderTransparentOnGpu,
                decision.Reasons,
                decision.Limitations,
            },
        }, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(outDir);
        var jsonPath = Path.Combine(outDir, "gpuprobe-report.json");
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"报告已写入: {jsonPath}");

        return 0;
    }

    private static void ReportVulkan(VulkanReport vulkan)
    {
        Console.WriteLine($"--- Vulkan ---");
        Console.WriteLine($"loader: {(vulkan.LoaderAvailable ? "可用" : "不可用")} {vulkan.LoaderVersion}" +
                          (vulkan.Failure is null ? "" : $" 失败: {vulkan.Failure}"));

        foreach (var device in vulkan.Devices)
        {
            Console.WriteLine();
            Console.WriteLine($"设备: {device.Name}  [{device.Type}]  Vulkan {device.ApiVersion}  驱动 {device.DriverVersion}");
            Console.WriteLine($"  vendor=0x{device.VendorId:X} device=0x{device.DeviceId:X}  显存 {device.DeviceLocalMemoryBytes / 1024 / 1024} MB");
            Console.WriteLine($"  队列族: {string.Join(", ", device.QueueFamilies)}");
            Console.WriteLine($"  窗口表面: {(device.SurfaceSupported ? "可用" : "不可用")}（{device.SurfaceExtent}，图像数 {device.MinImageCount}..{(device.MaxImageCount == 0 ? "不限" : device.MaxImageCount.ToString())}）");
            Console.WriteLine($"  合成 alpha: {device.CompositeAlpha}");
            Console.WriteLine($"  表面格式: {string.Join(" | ", device.SurfaceFormats)}");
            Console.WriteLine($"  呈现模式: {string.Join(" | ", device.PresentModes)}");
            Console.WriteLine($"  互操作: Win32外部内存={device.HasWin32ExternalMemory} 外部信号量={device.HasWin32ExternalSemaphore} 时间线信号量={device.HasTimelineSemaphore}");
            if (device.Failure is not null) Console.WriteLine($"  失败: {device.Failure}");
        }
        Console.WriteLine();
    }

    private static void ReportGl(GlReport gl)
    {
        Console.WriteLine("--- OpenGL ---");
        if (!gl.Available)
        {
            Console.WriteLine($"不可用: {gl.Failure}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"版本: {gl.Version}");
        Console.WriteLine($"渲染器: {gl.Renderer}  ({gl.Vendor})");
        Console.WriteLine($"像素格式: 颜色 {gl.ColorBits} 位，alpha {gl.AlphaBits} 位，深度 {gl.DepthBits} 位，双缓冲={gl.DoubleBuffered}");
        Console.WriteLine($"wglSwapIntervalEXT: {(gl.HasSwapControlExtension ? "有" : "无")}");
        Console.WriteLine(gl.PresentFrames > 0
            ? $"清屏+交换 {gl.PresentFrames} 帧: {gl.PresentMsPerFrame:F3} ms/帧（{1000.0 / gl.PresentMsPerFrame:F0} fps 上限）"
            : "未测帧耗时（frames=0）");
        Console.WriteLine();
    }

    private static void ReportDecision(RenderSurfaceDecision decision)
    {
        Console.WriteLine("=== 结论 ===");
        Console.WriteLine(RenderSurfacePolicy.Summarize(decision));
        Console.WriteLine($"选中设备: {decision.ChosenDevice ?? "(无)"}");
        foreach (var reason in decision.Reasons) Console.WriteLine($"  理由: {reason}");
        foreach (var limitation in decision.Limitations) Console.WriteLine($"  限制: {limitation}");
    }

    private static bool DirectCompositionAvailable()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system, "dcomp.dll"));
    }

    private static int ParseFrames(string[] args)
    {
        var value = ParseOption(args, "--frames");
        return int.TryParse(value, out var frames) && frames >= 0 ? frames : 120;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
