using System.Runtime.InteropServices;
using static GpuProbe.VulkanApi;

namespace GpuProbe;

internal sealed record VulkanDeviceReport(
    string Name,
    string Type,
    string ApiVersion,
    string DriverVersion,
    uint VendorId,
    uint DeviceId,
    long DeviceLocalMemoryBytes,
    string[] QueueFamilies,
    bool HasGraphicsQueue,
    bool SurfaceSupported,
    int CompositeAlphaFlags,
    string CompositeAlpha,
    string[] SurfaceFormats,
    string[] PresentModes,
    uint MinImageCount,
    uint MaxImageCount,
    string SurfaceExtent,
    string[] InteropExtensions,
    bool HasWin32ExternalMemory,
    bool HasWin32ExternalSemaphore,
    bool HasTimelineSemaphore,
    string? Failure)
{
    internal bool SupportsPremultipliedAlpha => (CompositeAlphaFlags & VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR) != 0;
}

internal sealed record VulkanReport(
    bool LoaderAvailable,
    string LoaderVersion,
    string InstanceApiVersion,
    string[] InstanceExtensions,
    VulkanDeviceReport[] Devices,
    string? Failure);

internal static class VulkanProbe
{
    private const uint VK_API_VERSION_1_0 = 1u << 22;

    internal static VulkanReport Run(IntPtr hwnd)
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var lib))
            return Empty("vulkan-1.dll 无法加载");

        NativeLibrary.Free(lib);

        uint loaderApi = 0;
        try
        {
            vkEnumerateInstanceVersion(out loaderApi);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1.1 loader: the symbol does not exist, version is implicitly 1.0.
        }

        var appName = Marshal.StringToHGlobalAnsi("XianlingGpuProbe");
        var engineName = Marshal.StringToHGlobalAnsi("XianlingGpuProbe");
        var app = new VkApplicationInfo
        {
            sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
            pApplicationName = appName,
            applicationVersion = 1,
            pEngineName = engineName,
            engineVersion = 1,
            apiVersion = loaderApi == 0 ? VK_API_VERSION_1_0 : loaderApi,
        };

        var requested = new[] { "VK_KHR_surface", "VK_KHR_win32_surface" };
        var extensionNames = MarshalStringArray(requested);

        var appHandle = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
        try
        {
            Marshal.StructureToPtr(app, appHandle, false);

            var createInfo = new VkInstanceCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = appHandle,
                enabledExtensionCount = (uint)requested.Length,
                ppEnabledExtensionNames = extensionNames.Pointer,
            };

            var result = vkCreateInstance(ref createInfo, IntPtr.Zero, out var instance);
            if (result != VK_SUCCESS)
                return Empty($"vkCreateInstance 失败，VkResult={result}");

            try
            {
                return Inspect(instance, loaderApi, hwnd);
            }
            finally
            {
                vkDestroyInstance(instance, IntPtr.Zero);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(appHandle);
            MarshalStringArrayFree(extensionNames);
            Marshal.FreeHGlobal(appName);
            Marshal.FreeHGlobal(engineName);
        }
    }

    private static VulkanReport Inspect(IntPtr instance, uint loaderApi, IntPtr hwnd)
    {
        uint deviceCount = 0;
        var rc = vkEnumeratePhysicalDevices(instance, ref deviceCount, null);
        if (rc != VK_SUCCESS || deviceCount == 0)
            return Empty($"没有枚举到物理设备（VkResult={rc}, count={deviceCount}）", loaderApi);

        var devices = new IntPtr[deviceCount];
        vkEnumeratePhysicalDevices(instance, ref deviceCount, devices);

        IntPtr surface = IntPtr.Zero;
        var surfaceInfo = new VkWin32SurfaceCreateInfoKHR
        {
            sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR,
            hinstance = Win32.GetModuleHandle(null),
            hwnd = hwnd,
        };
        var surfaceResult = vkCreateWin32SurfaceKHR(instance, ref surfaceInfo, IntPtr.Zero, out surface);

        try
        {
            var reports = new List<VulkanDeviceReport>();
            foreach (var device in devices)
                reports.Add(InspectDevice(device, surface, surfaceResult));
            return new VulkanReport(true, FormatVersion(loaderApi), FormatVersion(loaderApi),
                Array.Empty<string>(), reports.ToArray(), null);
        }
        finally
        {
            if (surface != IntPtr.Zero) vkDestroySurfaceKHR(instance, surface, IntPtr.Zero);
        }
    }

    private static VulkanDeviceReport InspectDevice(IntPtr device, IntPtr surface, uint surfaceResult)
    {
        string name;
        string typeName;
        uint apiVersion, driverVersion, vendorId, deviceId;

        // VkPhysicalDeviceProperties' fixed prefix: 4 x uint32, 1 x int32, char[256].
        // An oversized buffer avoids declaring the ~600-byte limits struct.
        var propsBuffer = Marshal.AllocHGlobal(1024);
        try
        {
            for (var i = 0; i < 1024; i++) Marshal.WriteByte(propsBuffer, i, 0);
            vkGetPhysicalDeviceProperties(device, propsBuffer);
            apiVersion = (uint)Marshal.ReadInt32(propsBuffer, 0);
            driverVersion = (uint)Marshal.ReadInt32(propsBuffer, 4);
            vendorId = (uint)Marshal.ReadInt32(propsBuffer, 8);
            deviceId = (uint)Marshal.ReadInt32(propsBuffer, 12);
            var typeIndex = Marshal.ReadInt32(propsBuffer, 16);
            typeName = typeIndex >= 0 && typeIndex < DeviceTypeNames.Length ? DeviceTypeNames[typeIndex] : $"TYPE({typeIndex})";
            name = Marshal.PtrToStringAnsi(propsBuffer + 20) ?? "(unnamed)";
        }
        finally
        {
            Marshal.FreeHGlobal(propsBuffer);
        }

        uint familyCount = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        var families = new VkQueueFamilyProperties[familyCount];
        if (familyCount > 0) vkGetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, families);

        var familyDescriptions = families
            .Select((f, i) => $"#{i} {DescribeQueueFlags(f.queueFlags)} x{f.queueCount}")
            .ToArray();
        var hasGraphics = families.Any(f => (f.queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0);

        vkGetPhysicalDeviceMemoryProperties(device, out var memory);
        long deviceLocal = 0;
        for (var i = 0; i < memory.memoryHeapCount && i < memory.memoryHeaps.Length; i++)
        {
            if ((memory.memoryHeaps[i].flags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) != 0)
                deviceLocal += (long)memory.memoryHeaps[i].size;
        }

        // Device extensions decide whether Vulkan can hand a texture to DirectComposition
        // (the only per-pixel-alpha GPU composition route on Windows).
        uint extCount = 0;
        vkEnumerateDeviceExtensionProperties(device, IntPtr.Zero, ref extCount, null);
        var extensions = new string[extCount];
        if (extCount > 0)
        {
            var extProps = new VkExtensionProperties[extCount];
            vkEnumerateDeviceExtensionProperties(device, IntPtr.Zero, ref extCount, extProps);
            extensions = extProps.Where(e => !string.IsNullOrEmpty(e.extensionName))
                                 .Select(e => e.extensionName).ToArray();
        }

        var win32Memory = extensions.Contains("VK_KHR_external_memory_win32")
                          || extensions.Contains("VK_EXT_external_memory_win32");
        var win32Semaphore = extensions.Contains("VK_KHR_external_semaphore_win32");
        var timeline = extensions.Contains("VK_KHR_timeline_semaphore")
                       || ApiAtLeast(apiVersion, 1, 2);
        var interop = extensions.Where(e => e.Contains("external_memory") || e.Contains("external_semaphore")
                                            || e.Contains("external_fence") || e.Contains("win32")
                                            || e.Contains("dxgi") || e.Contains("d3d"))
                                .OrderBy(e => e).ToArray();

        bool surfaceSupported = false;
        var alphaFlags = 0;
        string alphaText = "(surface 不可用)";
        var formats = Array.Empty<string>();
        var presentModes = Array.Empty<string>();
        uint minImages = 0, maxImages = 0;
        var extent = "(surface 不可用)";
        string? failure = null;

        if (surfaceResult != VK_SUCCESS || surface == IntPtr.Zero)
        {
            failure = $"vkCreateWin32SurfaceKHR 失败，VkResult={surfaceResult}";
        }
        else
        {
            uint presentFamily = uint.MaxValue;
            for (uint i = 0; i < familyCount; i++)
            {
                if ((families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) == 0) continue;
                if (vkGetPhysicalDeviceSurfaceSupportKHR(device, i, surface, out var supported) == VK_SUCCESS && supported != 0)
                {
                    presentFamily = i;
                    surfaceSupported = true;
                    break;
                }
            }

            var capsResult = vkGetPhysicalDeviceSurfaceCapabilitiesKHR(device, surface, out var caps);
            if (capsResult == VK_SUCCESS)
            {
                alphaFlags = caps.supportedCompositeAlpha;
                alphaText = DescribeCompositeAlpha(alphaFlags);
                minImages = caps.minImageCount;
                maxImages = caps.maxImageCount;
                extent = caps.currentExtent.width == uint.MaxValue
                    ? $"{caps.minImageExtent.width}x{caps.minImageExtent.height}..{caps.maxImageExtent.width}x{caps.maxImageExtent.height}"
                    : $"{caps.currentExtent.width}x{caps.currentExtent.height}";
            }
            else
            {
                failure = $"vkGetPhysicalDeviceSurfaceCapabilitiesKHR 失败，VkResult={capsResult}";
            }

            uint formatCount = 0;
            if (vkGetPhysicalDeviceSurfaceFormatsKHR(device, surface, ref formatCount, null) == VK_SUCCESS && formatCount > 0)
            {
                var surfaceFormats = new VkSurfaceFormatKHR[formatCount];
                vkGetPhysicalDeviceSurfaceFormatsKHR(device, surface, ref formatCount, surfaceFormats);
                formats = surfaceFormats.Select(f => $"format={f.format} colorspace={f.colorSpace}").ToArray();
            }

            uint modeCount = 0;
            if (vkGetPhysicalDeviceSurfacePresentModesKHR(device, surface, ref modeCount, null) == VK_SUCCESS && modeCount > 0)
            {
                var modes = new int[modeCount];
                vkGetPhysicalDeviceSurfacePresentModesKHR(device, surface, ref modeCount, modes);
                presentModes = modes.Select(DescribePresentMode).ToArray();
            }
        }

        return new VulkanDeviceReport(
            name, typeName, FormatVersion(apiVersion), FormatDriverVersion(vendorId, driverVersion), vendorId, deviceId,
            deviceLocal, familyDescriptions, hasGraphics, surfaceSupported,
            alphaFlags, alphaText, formats, presentModes, minImages, maxImages, extent,
            interop, win32Memory, win32Semaphore, timeline, failure);
    }

    private static bool ApiAtLeast(uint packed, int major, int minor)
    {
        var vkMajor = (int)(packed >> 22);
        var vkMinor = (int)((packed >> 12) & 0x3FF);
        return vkMajor > major || (vkMajor == major && vkMinor >= minor);
    }

    private static VulkanReport Empty(string failure, uint loaderApi = 0) =>
        new(false, FormatVersion(loaderApi), FormatVersion(loaderApi), Array.Empty<string>(),
            Array.Empty<VulkanDeviceReport>(), failure);

    private static (IntPtr Pointer, IntPtr[] Strings) MarshalStringArray(string[] values)
    {
        var strings = new IntPtr[values.Length];
        for (var i = 0; i < values.Length; i++) strings[i] = Marshal.StringToHGlobalAnsi(values[i]);
        var array = Marshal.AllocHGlobal(IntPtr.Size * values.Length);
        for (var i = 0; i < values.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
        return (array, strings);
    }

    private static void MarshalStringArrayFree((IntPtr Pointer, IntPtr[] Strings) handle)
    {
        foreach (var s in handle.Strings) Marshal.FreeHGlobal(s);
        Marshal.FreeHGlobal(handle.Pointer);
    }
}
