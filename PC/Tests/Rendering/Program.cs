using MyAiAssistant.Services.Rendering;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

static GpuDeviceFacts Device(
    string name = "Test GPU",
    string type = "DISCRETE_GPU",
    bool graphicsQueue = true,
    bool surface = true,
    bool premultipliedAlpha = false,
    bool win32ExternalMemory = true) =>
    new(name, type, "1.3.0", graphicsQueue, surface, premultipliedAlpha, win32ExternalMemory);

static RenderCapabilities Caps(
    bool vulkan = true,
    GpuDeviceFacts[]? devices = null,
    bool openGl = false,
    bool directComposition = true,
    string? failure = null) =>
    new(vulkan, devices ?? new[] { Device() }, openGl, openGl ? "4.6.0" : null, openGl ? "Test GL" : null, directComposition, failure);

// 1. A Vulkan surface that reports PRE_MULTIPLIED alpha can composite transparency itself.
{
    var decision = RenderSurfacePolicy.Decide(Caps(devices: new[] { Device(premultipliedAlpha: true) }));
    Assert(decision.OpaqueSurfaceBackend == RenderBackend.Vulkan, "premultiplied: opaque via Vulkan");
    Assert(decision.TransparentSurfaceBackend == RenderBackend.Vulkan, "premultiplied: transparent via Vulkan");
    Assert(decision.TransparentComposition == AlphaComposition.PerPixel, "premultiplied: per-pixel");
    Assert(decision.CanRenderTransparentOnGpu, "premultiplied: transparent on GPU");
}

// 2. This machine's actual result: Vulkan works, but only OPAQUE composite alpha.
//    Transparency must not be claimed, and the limitation must name the real routes.
{
    var decision = RenderSurfacePolicy.Decide(Caps(devices: new[] { Device() }));
    Assert(decision.OpaqueSurfaceBackend == RenderBackend.Vulkan, "opaque-only: opaque still Vulkan");
    Assert(decision.TransparentComposition == AlphaComposition.OpaqueOnly, "opaque-only: no per-pixel claim");
    Assert(!decision.CanRenderTransparentOnGpu, "opaque-only: transparent not on GPU");
    Assert(decision.Limitations.Any(l => l.Contains("分层窗口")), "opaque-only: layered window named");
    Assert(decision.Limitations.Any(l => l.Contains("DirectComposition")), "opaque-only: DirectComposition named");
}

// 3. Per-pixel alpha is a reason to switch away from the fastest device, and that switch
//    must be visible rather than silent.
{
    var discrete = Device("Fast Discrete");
    var integrated = Device("Integrated With Alpha", type: "INTEGRATED_GPU", premultipliedAlpha: true);
    var decision = RenderSurfacePolicy.Decide(Caps(devices: new[] { discrete, integrated }));
    Assert(decision.ChosenDevice == "Integrated With Alpha", "alpha wins over raw speed");
    Assert(decision.Reasons.Any(r => r.Contains("不是性能最优")), "alpha switch is explained");
}

// 4. Discrete is preferred over integrated when neither offers alpha.
{
    var decision = RenderSurfacePolicy.Decide(Caps(devices: new[]
    {
        Device("Integrated", type: "INTEGRATED_GPU"),
        Device("Discrete", type: "DISCRETE_GPU"),
    }));
    Assert(decision.ChosenDevice == "Discrete", "discrete preferred");
    Assert(decision.Reasons.Any(r => r.Contains("回退")), "fallback device noted");
}

// 5. Devices that cannot present to a window are rejected, and said so out loud.
{
    var decision = RenderSurfacePolicy.Decide(Caps(devices: new[]
    {
        Device("No Surface", surface: false),
        Device("No Graphics Queue", graphicsQueue: false),
    }));
    Assert(decision.OpaqueSurfaceBackend == RenderBackend.None, "unusable devices yield no backend");
    Assert(decision.Limitations.Any(l => l.Contains("No Surface")), "surface rejection reported");
    Assert(decision.Limitations.Any(l => l.Contains("No Graphics Queue")), "queue rejection reported");
}

// 6. OpenGL alone renders opaque surfaces but never claims transparency.
{
    var decision = RenderSurfacePolicy.Decide(Caps(vulkan: false, devices: Array.Empty<GpuDeviceFacts>(), openGl: true));
    Assert(decision.OpaqueSurfaceBackend == RenderBackend.OpenGL, "gl: opaque via OpenGL");
    Assert(decision.TransparentComposition == AlphaComposition.OpaqueOnly, "gl: no per-pixel claim");
    Assert(decision.Limitations.Any(l => l.Contains("OpenGL")), "gl: limitation recorded");
}

// 7. No GPU at all: the surfaces stay where they are today, and that is stated.
{
    var decision = RenderSurfacePolicy.Decide(Caps(vulkan: false, devices: Array.Empty<GpuDeviceFacts>(), openGl: false, directComposition: false));
    Assert(decision.OpaqueSurfaceBackend == RenderBackend.None, "none: no backend");
    Assert(decision.TransparentSurfaceBackend == RenderBackend.None, "none: no transparent backend");
    Assert(decision.Limitations.Any(l => l.Contains("WebView2")), "none: current path named");
    Assert(decision.Limitations.Any(l => l.Contains("DirectComposition 不可用")), "none: cpu-only route named");
}

// 8. A failed probe degrades into a recorded limitation, never an exception or a claim.
{
    var decision = RenderSurfacePolicy.Decide(Caps(vulkan: false, devices: Array.Empty<GpuDeviceFacts>(), failure: "vkCreateInstance 失败"));
    Assert(decision.Limitations.Any(l => l.Contains("探测本身未完成")), "failure surfaced");
    Assert(decision.OpaqueSurfaceBackend == RenderBackend.None, "failure: no backend claimed");
}

// 9. Empty/null inputs must not throw.
{
    var empty = RenderSurfacePolicy.Decide(Caps(devices: Array.Empty<GpuDeviceFacts>()));
    Assert(empty.OpaqueSurfaceBackend == RenderBackend.None, "empty device list is safe");

    var nulled = RenderSurfacePolicy.Decide(new RenderCapabilities(true, null!, false, null, null, true, null));
    Assert(nulled.OpaqueSurfaceBackend == RenderBackend.None, "null device list is safe");
}

// 10. The one-line verdict never claims transparency the capabilities do not support.
{
    var opaque = RenderSurfacePolicy.Decide(Caps(devices: new[] { Device() }));
    Assert(!RenderSurfacePolicy.Summarize(opaque).Contains("逐像素透明（"), "summary does not overclaim");

    var transparent = RenderSurfacePolicy.Decide(Caps(devices: new[] { Device(premultipliedAlpha: true) }));
    Assert(RenderSurfacePolicy.Summarize(transparent).Contains("逐像素透明"), "summary reports real transparency");
}

Console.WriteLine($"All {checks} rendering surface checks passed.");
