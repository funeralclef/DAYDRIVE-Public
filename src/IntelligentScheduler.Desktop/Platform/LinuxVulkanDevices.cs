using System.Runtime.InteropServices;

namespace IntelligentScheduler.Desktop.Platform;

internal sealed record LinuxVulkanDevice(uint Vendor, uint Device, string Name, long LocalBytes, bool Integrated);

/// <summary>Enumerates hardware without a display, model download, Vulkan SDK or vulkaninfo.</summary>
internal static class LinuxVulkanDevices
{
    public static IReadOnlyList<LinuxVulkanDevice> Read()
    {
        var devices = new List<LinuxVulkanDevice>();
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) return devices;
        nint instance = 0, properties = 0, memory = 0;
        try
        {
            var create = new InstanceCreateInfo { Type = 1 };
            if (vkCreateInstance(ref create, 0, out instance) != 0) return devices;
            // The Vulkan 1.0 structs have a fixed ABI. On 64-bit Linux properties occupy
            // 824 bytes (we only read the 276-byte prefix); memory properties occupy 520.
            properties = Marshal.AllocHGlobal(1024);
            memory = Marshal.AllocHGlobal(520);
            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) < 0 || count == 0) return devices;
            var handles = new nint[Math.Min(count, 256)];
            count = (uint)handles.Length;
            if (vkEnumeratePhysicalDevices(instance, ref count, handles) < 0) return devices;
            foreach (var handle in handles.Take((int)Math.Min(count, (uint)handles.Length)))
            {
                vkGetPhysicalDeviceProperties(handle, properties);
                var type = Marshal.ReadInt32(properties, 16);
                // CPU rasterisers (llvmpipe/lavapipe) are not graphics cards.
                if (type is not (1 or 2 or 3)) continue;
                var name = Marshal.PtrToStringUTF8(properties + 20);
                if (string.IsNullOrWhiteSpace(name)) continue;
                vkGetPhysicalDeviceMemoryProperties(handle, memory);
                var heapCount = Math.Clamp(Marshal.ReadInt32(memory, 260), 0, 16);
                long local = 0;
                for (var i = 0; i < heapCount; i++)
                {
                    var offset = 264 + i * 16;
                    if ((Marshal.ReadInt32(memory, offset + 8) & 1) != 0)
                        local += Math.Max(0, Marshal.ReadInt64(memory, offset));
                }
                // DEVICE_LOCAL on an integrated GPU is shared system RAM, not dedicated VRAM.
                devices.Add(new(unchecked((uint)Marshal.ReadInt32(properties, 8)),
                    unchecked((uint)Marshal.ReadInt32(properties, 12)), name, type == 1 ? 0 : local, type == 1));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Missing loader/driver: sysfs and the vendor's tools still identify installed cards.
        }
        finally
        {
            if (properties != 0) Marshal.FreeHGlobal(properties);
            if (memory != 0) Marshal.FreeHGlobal(memory);
            if (instance != 0) vkDestroyInstance(instance, 0);
        }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public uint Type;
        public nint Next;
        public uint Flags;
        public nint ApplicationInfo;
        public uint LayerCount;
        public nint Layers;
        public uint ExtensionCount;
        public nint Extensions;
    }

    private const string Vulkan = "libvulkan.so.1";
    [DllImport(Vulkan)] private static extern int vkCreateInstance(ref InstanceCreateInfo info, nint allocator, out nint instance);
    [DllImport(Vulkan)] private static extern void vkDestroyInstance(nint instance, nint allocator);
    [DllImport(Vulkan)] private static extern int vkEnumeratePhysicalDevices(nint instance, ref uint count, [Out] nint[]? devices);
    [DllImport(Vulkan)] private static extern void vkGetPhysicalDeviceProperties(nint device, nint properties);
    [DllImport(Vulkan)] private static extern void vkGetPhysicalDeviceMemoryProperties(nint device, nint properties);
}
