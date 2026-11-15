namespace MyAiAssistant.Services.Rendering;

/// <summary>
/// What the platform can actually do for a GPU-rendered surface.
/// Deliberately limited to facts a probe can observe, so nothing here is inferred from
/// "the driver usually supports it".
/// </summary>
public enum RenderBackend
{
    None = 0,
    OpenGL,
    Vulkan
}

/// <summary>
/// How a surface's pixels can reach the desktop. <see cref="OpaqueOnly"/> means the
/// surface can be shown, but not blended per-pixel over what is behind it.
/// </summary>
public enum AlphaComposition
{
    Unavailable = 0,
    OpaqueOnly,
    PerPixel
}

public sealed record GpuDeviceFacts(
    string Name,
    string Type,
    string ApiVersion,
    bool HasGraphicsQueue,
    bool SurfaceSupported,
    bool SupportsPremultipliedAlpha,
    bool HasWin32ExternalMemory)
{
    /// <summary>Discrete GPUs are preferred; everything else is a fallback.</summary>
    public int Rank => Type switch
    {
        "DISCRETE_GPU" => 3,
        "INTEGRATED_GPU" => 2,
        "VIRTUAL_GPU" => 1,
        _ => 0
    };
}

public sealed record RenderCapabilities(
    bool VulkanLoaderAvailable,
    IReadOnlyList<GpuDeviceFacts> VulkanDevices,
    bool OpenGLAvailable,
    string? OpenGlVersion,
    string? OpenGlRenderer,
    bool DirectCompositionAvailable,
    string? Failure);

public sealed record RenderSurfaceDecision(
    RenderBackend OpaqueSurfaceBackend,
    RenderBackend TransparentSurfaceBackend,
    AlphaComposition TransparentComposition,
    string? ChosenDevice,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Limitations)
{
    public bool CanRenderTransparentOnGpu => TransparentComposition == AlphaComposition.PerPixel;
}

/// <summary>
/// Picks a render backend from observed capabilities. Every downgrade is recorded in
/// <see cref="RenderSurfaceDecision.Limitations"/> rather than silently absorbed, because
/// the checklist forbids presenting an unverified path as a stable feature.
/// </summary>
public static class RenderSurfacePolicy
{
    public static RenderSurfaceDecision Decide(RenderCapabilities capabilities)
    {
        var reasons = new List<string>();
        var limitations = new List<string>();

        if (!string.IsNullOrWhiteSpace(capabilities.Failure))
            limitations.Add($"探测本身未完成：{capabilities.Failure}");

        var usable = (capabilities.VulkanDevices ?? Array.Empty<GpuDeviceFacts>())
            .Where(d => d.HasGraphicsQueue && d.SurfaceSupported)
            .OrderByDescending(d => d.Rank)
            .ThenByDescending(d => d.SupportsPremultipliedAlpha)
            .ToList();

        var discarded = (capabilities.VulkanDevices ?? Array.Empty<GpuDeviceFacts>())
            .Where(d => !d.HasGraphicsQueue || !d.SurfaceSupported)
            .ToList();
        foreach (var device in discarded)
            limitations.Add($"{device.Name} 不能用于窗口表面（图形队列={device.HasGraphicsQueue}，表面={device.SurfaceSupported}）");

        RenderBackend opaqueBackend;
        string? chosenDevice = null;

        if (capabilities.VulkanLoaderAvailable && usable.Count > 0)
        {
            var best = usable[0];
            opaqueBackend = RenderBackend.Vulkan;
            chosenDevice = best.Name;
            reasons.Add($"Vulkan 可用，选用 {best.Name}（{best.Type}，Vulkan {best.ApiVersion}）");

            if (usable.Count > 1)
                reasons.Add($"另有 {usable.Count - 1} 个 Vulkan 设备可作为回退");
        }
        else if (capabilities.OpenGLAvailable)
        {
            opaqueBackend = RenderBackend.OpenGL;
            reasons.Add($"Vulkan 不可用，退回 OpenGL（{capabilities.OpenGlRenderer ?? "未知渲染器"}）");
            if (!capabilities.VulkanLoaderAvailable)
                limitations.Add("本机没有可用的 Vulkan loader");
            else
                limitations.Add("有 Vulkan loader 但没有可用的窗口表面设备");
        }
        else
        {
            opaqueBackend = RenderBackend.None;
            reasons.Add("Vulkan 与 OpenGL 均不可用，没有 GPU 渲染面可用");
            limitations.Add("没有 GPU 渲染面：3D/Live2D/MMD 表面只能继续走现有 WebView2/WPF 路径");
        }

        // Per-pixel alpha over the desktop is the deciding requirement for the orb, the
        // dynamic island and a transparent Live2D window.
        var alphaCapable = usable.FirstOrDefault(d => d.SupportsPremultipliedAlpha);

        if (alphaCapable is not null)
        {
            if (usable.Count > 0 && !ReferenceEquals(alphaCapable, usable[0]))
                reasons.Add($"为逐像素透明改用 {alphaCapable.Name}，它不是性能最优的那个设备");
            reasons.Add($"{alphaCapable.Name} 的窗口表面报告 PRE_MULTIPLIED 合成 alpha");
            return Build(RenderBackend.Vulkan, AlphaComposition.PerPixel, opaqueBackend,
                alphaCapable.Name, reasons, limitations);
        }

        if (usable.Count > 0)
            limitations.Add("选中的 Vulkan 表面未报告 PRE_MULTIPLIED 合成 alpha，无法直接与桌面逐像素混合");

        if (capabilities.OpenGLAvailable)
            limitations.Add("OpenGL/WGL 没有等价的合成 alpha 概念，不能替代这条路");

        limitations.Add("Windows 上逐像素透明只有两条路：分层窗口 UpdateLayeredWindow（像素由 CPU 提供）或 DirectComposition + D3D11 预乘 alpha 交换链（像素由 GPU 提供）");
        if (!capabilities.DirectCompositionAvailable)
            limitations.Add("本机 DirectComposition 不可用，GPU 侧那条路不存在，只剩每帧一次 CPU 拷贝的 UpdateLayeredWindow");
        else if (usable.Count > 0)
            limitations.Add($"Vulkan 要参与透明合成，需要经 Win32 外部内存互操作把纹理交给 DComp（{usable[0].Name} 支持外部内存={usable[0].HasWin32ExternalMemory}）");

        return Build(RenderBackend.None, AlphaComposition.OpaqueOnly, opaqueBackend,
            chosenDevice, reasons, limitations);
    }

    private static RenderSurfaceDecision Build(
        RenderBackend transparentBackend,
        AlphaComposition composition,
        RenderBackend opaqueBackend,
        string? chosenDevice,
        List<string> reasons,
        List<string> limitations) =>
        new(opaqueBackend, transparentBackend, composition, chosenDevice, reasons, limitations);

    /// <summary>Short single-line verdict, used by the probe's report header.</summary>
    public static string Summarize(RenderSurfaceDecision decision)
    {
        var transparent = decision.TransparentComposition switch
        {
            AlphaComposition.PerPixel => $"GPU 逐像素透明（{decision.TransparentSurfaceBackend}）",
            AlphaComposition.OpaqueOnly => "仅不透明 GPU 表面，透明仍需分层窗口",
            _ => "无 GPU 渲染面"
        };
        return $"不透明表面={decision.OpaqueSurfaceBackend}；透明表面={transparent}";
    }
}
