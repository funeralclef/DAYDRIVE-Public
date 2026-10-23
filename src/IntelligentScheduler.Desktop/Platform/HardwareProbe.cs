using System.Runtime.InteropServices;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// What this machine has for running a model: memory, cores of each kind, and graphics cards.
///
/// Asked of the operating system rather than inferred from the process. The process sees
/// logical processors only, which doubles the cores of a hyper-threaded machine and counts a
/// hybrid processor's efficiency cores as if they were as quick as the fast ones — and llama.cpp
/// generates more slowly, not more quickly, when it is told to wait for them.
/// </summary>
public static class HardwareProbe
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly Lazy<(int Physical, int Performance)> CoreCounts = new(ReadCores);
    private static readonly object Gate = new();
    private static IReadOnlyList<GpuAdapter>? _adapters;

    /// <summary>Physical cores, or half the logical processors when the system will not say.</summary>
    public static int PhysicalCores => CoreCounts.Value.Physical > 0
        ? CoreCounts.Value.Physical
        : Math.Max(1, Environment.ProcessorCount / 2);

    /// <param name="gpuEnginePath">A GPU build of llama-server, asked what is free on the card; null to skip that.</param>
    public static HardwareProfile Read(string? gpuEnginePath)
    {
        var (physical, performance) = CoreCounts.Value;
        var adapters = Adapters();
        if (gpuEnginePath is not null && GpuDevice.ReadNow(gpuEnginePath) is { Name: not null } device)
            adapters = Refine(adapters, device.Name, device.TotalBytes, device.FreeBytes);
        return new HardwareProfile(SystemMemory.TotalBytes, SystemMemory.AvailableBytes,
            physical, performance, Math.Max(1, Environment.ProcessorCount), adapters);
    }

    /// <summary>
    /// Folds in what the engine said: the card it will actually use, and what is free on it now —
    /// the one figure the system's adapter list does not carry.
    /// </summary>
    private static IReadOnlyList<GpuAdapter> Refine(IReadOnlyList<GpuAdapter> adapters, string name, long total, long free)
    {
        var match = adapters.FirstOrDefault(adapter => adapter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? adapters.FirstOrDefault(adapter => name.Contains(adapter.Name, StringComparison.OrdinalIgnoreCase) ||
                                                          adapter.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        // A driver or container may expose a device only to the engine.
        if (match is null)
            return [.. adapters, new GpuAdapter(name, total, free, IsIntegrated: total < 2 * GiB)];

        // A built-in adapter reports the shared system memory it may borrow as its own; it keeps
        // the small figure the system gave it, which is the honest one.
        var refined = match with
        {
            DedicatedBytes = match.IsIntegrated ? match.DedicatedBytes : Math.Max(match.DedicatedBytes, total),
            FreeBytes = free
        };
        return adapters.Select(adapter => ReferenceEquals(adapter, match) ? refined : adapter).ToList();
    }

    private static IReadOnlyList<GpuAdapter> Adapters()
    {
        // Linux also refreshes free VRAM and sees cards/drivers that appeared after startup.
        if (OperatingSystem.IsLinux()) return LinuxGraphicsProbe.Read();
        lock (Gate)
        {
            _adapters ??= OperatingSystem.IsWindows() ? ReadDxgiAdapters() : [];
            return _adapters;
        }
    }

    // ---------------------------------------------------------------- cores

    private static (int Physical, int Performance) ReadCores()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsCores();
            if (OperatingSystem.IsLinux()) return LinuxCores();
        }
        catch (Exception)
        {
            // Half the logical processors is what an unanswered question gets.
        }
        return (0, 0);
    }

    /// <summary>
    /// One record per physical core, each carrying an efficiency class. On a hybrid processor the
    /// performance cores carry the highest class; on anything else every core carries the same one.
    /// </summary>
    private static (int Physical, int Performance) WindowsCores()
    {
        const int relationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return (0, 0);

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length)) return (0, 0);
            var classes = new List<byte>();
            for (long offset = 0; offset < length;)
            {
                var entry = buffer + (nint)offset;
                var size = Marshal.ReadInt32(entry, 4);
                if (size <= 0) break;
                // Relationship at 0, size at 4, then the core's flags at 8 and its class at 9.
                if (Marshal.ReadInt32(entry, 0) == relationProcessorCore) classes.Add(Marshal.ReadByte(entry, 9));
                offset += size;
            }
            if (classes.Count == 0) return (0, 0);
            var fastest = classes.Max();
            return (classes.Count, classes.Count(value => value == fastest));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Distinct package and core pairs; Linux publishes no efficiency classes here.</summary>
    private static (int Physical, int Performance) LinuxCores()
    {
        var cores = new HashSet<(string Package, string Core)>();
        var package = "0";
        foreach (var line in File.ReadLines("/proc/cpuinfo"))
        {
            var parts = line.Split(':', 2);
            if (parts.Length < 2) continue;
            var key = parts[0].Trim();
            if (key == "physical id") package = parts[1].Trim();
            else if (key == "core id") cores.Add((package, parts[1].Trim()));
        }
        return (cores.Count, cores.Count);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    // ---------------------------------------------------------------- graphics

    private const uint SoftwareAdapterFlag = 2;
    private const uint MicrosoftVendorId = 0x1414;

    /// <summary>
    /// Every adapter DXGI lists, with its own memory. A card with less than a gigabyte of its own
    /// is one built into the processor, borrowing system memory, and is marked as such.
    /// </summary>
    private static IReadOnlyList<GpuAdapter> ReadDxgiAdapters()
    {
        var adapters = new List<GpuAdapter>();
        IDxgiFactory1? factory = null;
        try
        {
            var iid = typeof(IDxgiFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory is null) return adapters;
            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out var adapter) < 0 || adapter is null) break;
                try
                {
                    if (adapter.GetDesc1(out var description) < 0) continue;
                    // The software rasteriser is always listed and never worth running a model on.
                    if ((description.Flags & SoftwareAdapterFlag) != 0 || description.VendorId == MicrosoftVendorId) continue;
                    var dedicated = (long)description.DedicatedVideoMemory;
                    adapters.Add(new GpuAdapter(description.Description.Trim(), dedicated, 0, IsIntegrated: dedicated < GiB));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or
                                               EntryPointNotFoundException or InvalidCastException or MarshalDirectiveException)
        {
            // No DXGI — a server edition, a remote session. The engine's own list still answers.
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
        return adapters;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDxgiFactory1? factory);

    // Only the order of these methods matters: the runtime maps them onto the COM table slot by
    // slot, and the ones never called are declared bare simply to hold their places.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDxgiFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDxgiAdapter1? adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDxgiAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDescription description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }
}
