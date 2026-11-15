using System.Runtime.InteropServices;

namespace GpuProbe;

/// <summary>
/// Minimal Vulkan 1.0 + VK_KHR_win32_surface / VK_KHR_surface bindings.
/// Deliberately hand-rolled: adding a Vulkan wrapper package would be a dependency
/// decision, and this probe only needs the calls below.
/// </summary>
internal static class VulkanApi
{
    private const string Lib = "vulkan-1.dll";

    internal const int VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    internal const int VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    internal const int VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR = 1000009000;

    internal const uint VK_SUCCESS = 0;
    internal const uint VK_INCOMPLETE = 5;

    internal const int VK_MAX_PHYSICAL_DEVICE_NAME_SIZE = 256;
    internal const int VK_MAX_MEMORY_TYPES = 32;
    internal const int VK_MAX_MEMORY_HEAPS = 16;

    internal const int VK_PHYSICAL_DEVICE_TYPE_OTHER = 0;
    internal const int VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU = 1;
    internal const int VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU = 2;
    internal const int VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU = 3;
    internal const int VK_PHYSICAL_DEVICE_TYPE_CPU = 4;

    internal const int VK_QUEUE_GRAPHICS_BIT = 0x00000001;
    internal const int VK_QUEUE_COMPUTE_BIT = 0x00000002;
    internal const int VK_QUEUE_TRANSFER_BIT = 0x00000004;
    internal const int VK_QUEUE_SPARSE_BINDING_BIT = 0x00000008;
    internal const int VK_QUEUE_PROTECTED_BIT = 0x00000010;

    internal const int VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT = 0x00000001;

    // VkCompositeAlphaFlagBitsKHR
    internal const int VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR = 0x00000001;
    internal const int VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR = 0x00000002;
    internal const int VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR = 0x00000004;
    internal const int VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR = 0x00000008;

    internal static readonly string[] DeviceTypeNames =
    {
        "OTHER", "INTEGRATED_GPU", "DISCRETE_GPU", "VIRTUAL_GPU", "CPU",
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkApplicationInfo
    {
        public int sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkInstanceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public int flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkWin32SurfaceCreateInfoKHR
    {
        public int sType;
        public IntPtr pNext;
        public int flags;
        public IntPtr hinstance;
        public IntPtr hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkExtent2D
    {
        public uint width;
        public uint height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkExtent3D
    {
        public uint width;
        public uint height;
        public uint depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkQueueFamilyProperties
    {
        public int queueFlags;
        public uint queueCount;
        public uint timestampValidBits;
        public VkExtent3D minImageTransferGranularity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkMemoryType
    {
        public int propertyFlags;
        public uint heapIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkMemoryHeap
    {
        public ulong size;
        public int flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPhysicalDeviceMemoryProperties
    {
        public uint memoryTypeCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = VK_MAX_MEMORY_TYPES)]
        public VkMemoryType[] memoryTypes;

        public uint memoryHeapCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = VK_MAX_MEMORY_HEAPS)]
        public VkMemoryHeap[] memoryHeaps;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSurfaceCapabilitiesKHR
    {
        public uint minImageCount;
        public uint maxImageCount;
        public VkExtent2D currentExtent;
        public VkExtent2D minImageExtent;
        public VkExtent2D maxImageExtent;
        public uint maxImageArrayLayers;
        public int supportedTransforms;
        public int currentTransform;
        public int supportedCompositeAlpha;
        public int supportedUsageFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSurfaceFormatKHR
    {
        public uint format;
        public uint colorSpace;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct VkExtensionProperties
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string extensionName;

        public uint specVersion;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkEnumerateDeviceExtensionProperties(IntPtr physicalDevice, IntPtr layerName, ref uint count, [Out] VkExtensionProperties[]? properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkEnumerateInstanceVersion(out uint apiVersion);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkEnumerateInstanceExtensionProperties(IntPtr layerName, ref uint count, [Out] IntPtr[]? properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    /// <summary>
    /// VkPhysicalDeviceProperties is ~800 bytes and its tail (limits/sparseProperties) is not
    /// needed here, so the caller passes an oversized buffer and reads the fixed-size prefix.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern void vkGetPhysicalDeviceProperties(IntPtr physicalDevice, IntPtr properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr physicalDevice, ref uint count, [Out] VkQueueFamilyProperties[]? properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr physicalDevice, out VkPhysicalDeviceMemoryProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkCreateWin32SurfaceKHR(IntPtr instance, ref VkWin32SurfaceCreateInfoKHR createInfo, IntPtr allocator, out IntPtr surface);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern void vkDestroySurfaceKHR(IntPtr instance, IntPtr surface, IntPtr allocator);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkGetPhysicalDeviceSurfaceCapabilitiesKHR(IntPtr physicalDevice, IntPtr surface, out VkSurfaceCapabilitiesKHR capabilities);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkGetPhysicalDeviceSurfaceFormatsKHR(IntPtr physicalDevice, IntPtr surface, ref uint count, [Out] VkSurfaceFormatKHR[]? formats);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkGetPhysicalDeviceSurfacePresentModesKHR(IntPtr physicalDevice, IntPtr surface, ref uint count, [Out] int[]? modes);

    [DllImport(Lib, CallingConvention = CallingConvention.Winapi)]
    internal static extern uint vkGetPhysicalDeviceSurfaceSupportKHR(IntPtr physicalDevice, uint queueFamilyIndex, IntPtr surface, out int supported);

    internal static string FormatVersion(uint packed) =>
        $"{packed >> 22}.{(packed >> 12) & 0x3FF}.{packed & 0xFFF}";

    /// <summary>
    /// VkPhysicalDeviceProperties.driverVersion is vendor-encoded, not a VkVersion.
    /// Reporting the generic decode for NVIDIA/Intel produces a plausible but wrong number,
    /// which is worse than not reporting it at all.
    /// </summary>
    internal static string FormatDriverVersion(uint vendorId, uint packed) => vendorId switch
    {
        0x10DE => $"{(packed >> 22) & 0x3FF}.{(packed >> 14) & 0xFF}.{(packed >> 6) & 0xFF}.{packed & 0x3F}",
        0x8086 => $"{(packed >> 14) & 0x3FFFF}.{packed & 0x3FFF}",
        _ => FormatVersion(packed)
    };

    internal static string DescribeCompositeAlpha(int flags)
    {
        var parts = new List<string>();
        if ((flags & VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR) != 0) parts.Add("OPAQUE");
        if ((flags & VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR) != 0) parts.Add("PRE_MULTIPLIED");
        if ((flags & VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR) != 0) parts.Add("POST_MULTIPLIED");
        if ((flags & VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR) != 0) parts.Add("INHERIT");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }

    internal static string DescribePresentMode(int mode) => mode switch
    {
        0 => "IMMEDIATE",
        1 => "MAILBOX",
        2 => "FIFO",
        3 => "FIFO_RELAXED",
        1000361000 => "SHARED_DEMAND_REFRESH",
        1000361001 => "SHARED_CONTINUOUS_REFRESH",
        _ => $"UNKNOWN({mode})",
    };

    internal static string DescribeQueueFlags(int flags)
    {
        var parts = new List<string>();
        if ((flags & VK_QUEUE_GRAPHICS_BIT) != 0) parts.Add("GRAPHICS");
        if ((flags & VK_QUEUE_COMPUTE_BIT) != 0) parts.Add("COMPUTE");
        if ((flags & VK_QUEUE_TRANSFER_BIT) != 0) parts.Add("TRANSFER");
        if ((flags & VK_QUEUE_SPARSE_BINDING_BIT) != 0) parts.Add("SPARSE");
        if ((flags & VK_QUEUE_PROTECTED_BIT) != 0) parts.Add("PROTECTED");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }
}
