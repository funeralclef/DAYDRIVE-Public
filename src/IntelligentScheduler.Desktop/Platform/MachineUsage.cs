using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// One Windows performance counter that publishes an instance per process, read back for a
/// single process id.
///
/// Both graphics figures the monitor shows are counters of this shape — the instances are named
/// "pid_1234_luid_0x…_engtype_3D" and "pid_1234_luid_0x…_phys_0" — and both are read the same
/// way: collect the query, walk the formatted array, keep the instances whose name begins with
/// this process. It is the same source Task Manager reads its own graphics columns from, which
/// is the point: a monitor that disagrees with Task Manager about whether the card is busy is
/// not a monitor.
/// </summary>
/// <param name="needsBaseline">
/// True for a rate counter, whose first collection is only the baseline the second is measured
/// against and reads back as zeroes. A plain gauge — how many bytes are held right now — has an
/// answer on its first collection and must not be made to wait a second for it.
/// </param>
internal sealed class PdhInstanceCounter(string counterPath, bool needsBaseline) : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const int MaxConsecutiveFailures = 3;

    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _counter;
    private int _failures;
    private bool _disposed;

    /// <summary>Nothing but Windows keeps these counters; elsewhere the column has no figure.</summary>
    public bool IsSupported => OperatingSystem.IsWindows() && _failures < MaxConsecutiveFailures;

    /// <summary>True once a collection has actually produced a reading rather than a baseline.</summary>
    public bool HasRead { get; private set; }

    private bool _collected;

    /// <summary>
    /// Collects the query and hands every instance belonging to <paramref name="processId"/> to
    /// <paramref name="row"/>. False when there was nothing to read — an unsupported platform, a
    /// machine with no such counters, or a collection that found no instances at all, which for
    /// a card with nothing to do is the normal answer rather than a failure.
    /// </summary>
    public bool Read(int processId, Action<string, double> row)
    {
        if (!IsSupported || _disposed) return false;
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                if (!EnsureQuery()) return false;
                if (PdhCollectQueryData(_query) != 0)
                {
                    // No data at all is not a failure worth giving up over: a card with nothing
                    // to do publishes no instances.
                    _collected = true;
                    return false;
                }
                var baseline = needsBaseline && !_collected;
                _collected = true;
                if (baseline) return false;
                ReadInstances(processId, row);
                HasRead = true;
                _failures = 0;
                return true;
            }
            catch (Exception)
            {
                // A machine whose PDH refuses to answer is told once and then left alone.
                _failures++;
                Close();
                return false;
            }
        }
    }

    private bool EnsureQuery()
    {
        if (_query != IntPtr.Zero) return true;
        if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out var query) != 0) { _failures++; return false; }
        // The English name is asked for by name: these counters are localised, and the shell
        // runs in whatever language the machine is in.
        if (PdhAddEnglishCounter(query, counterPath, IntPtr.Zero, out var counter) != 0)
        {
            PdhCloseQuery(query);
            _failures++;
            return false;
        }
        _query = query;
        _counter = counter;
        _collected = false;
        HasRead = false;
        return true;
    }

    private void ReadInstances(int processId, Action<string, double> row)
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out var count, buffer) != 0) return;

            // The item is a pointer to a name followed by the counter union. The union's double
            // is aligned to its own width, which is why the offset is not simply the pointer
            // size plus four.
            var itemSize = IntPtr.Size == 8 ? 24 : 16;
            var valueOffset = IntPtr.Size == 8 ? 16 : 8;
            var prefix = "pid_" + processId.ToString(CultureInfo.InvariantCulture) + "_";

            for (var index = 0; index < count; index++)
            {
                var item = buffer + index * itemSize;
                var namePointer = Marshal.ReadIntPtr(item);
                if (namePointer == IntPtr.Zero) continue;
                var name = Marshal.PtrToStringUni(namePointer);
                if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, valueOffset));
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) continue;
                row(name, value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Close()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
        _collected = false;
        HasRead = false;
    }

    /// <summary>Called when the model stops, so the next run measures from its own baseline.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _collected = false;
            HasRead = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>
/// What the graphics card is actually doing for one process: how busy it is, and how much of
/// its memory the process is holding.
///
/// Both readings used to come out of what llama.cpp printed on the way up, and both stopped
/// working the moment the engine changed how it logs. The current builds print six lines at
/// startup and none of them mentions the device, the layers or the buffers, so the monitor's
/// GPU column sat at "not in use" and the video-memory row never appeared at all — on a machine
/// where the model was in fact on the card using close to six gigabytes of it.
///
/// Neither figure comes from the log now. Utilisation is summed within an engine type and the
/// busiest engine wins, rather than everything being added together: a card 40% busy on 3D and
/// 40% on video decode is 40% busy, not 80%. Memory is the opposite — the process's dedicated
/// allocation is spread over one instance per adapter and those do add up.
/// </summary>
public sealed class GpuUsageSampler : IDisposable
{
    /// <summary>A rate counter needs two collections before it means anything.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(700);

    private readonly PdhInstanceCounter _engine = new(@"\GPU Engine(*)\Utilization Percentage", needsBaseline: true);
    private readonly PdhInstanceCounter _memory = new(@"\GPU Process Memory(*)\Dedicated Usage", needsBaseline: false);
    private readonly Dictionary<string, double> _byEngine = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private double _lastUsage;
    private long _lastMemory;
    private bool _disposed;

    public bool IsSupported => _engine.IsSupported;

    /// <summary>
    /// The share of the busiest graphics engine this process is using, and the video memory it
    /// holds. Null usage means there is no figure to be had — an unsupported platform, or the
    /// first call, which only primes the counter. Both are kept between samples so the reading
    /// does not blink to nothing on the ticks where the counter has not moved on.
    /// </summary>
    public (double? UsagePercent, long VideoMemoryBytes) Sample(int processId)
    {
        if (_disposed || !IsSupported) return (null, 0);
        lock (_gate)
        {
            if (_disposed) return (null, 0);
            var now = DateTimeOffset.UtcNow;
            if (_engine.HasRead && now - _lastSample < MinimumInterval)
                return (_lastUsage, _lastMemory);
            _lastSample = now;

            _byEngine.Clear();
            if (_engine.Read(processId, (name, value) =>
                {
                    var engine = name.LastIndexOf("engtype_", StringComparison.Ordinal) is var at and >= 0
                        ? name[at..]
                        : name;
                    _byEngine[engine] = _byEngine.GetValueOrDefault(engine) + value;
                }))
                _lastUsage = _byEngine.Count == 0 ? 0 : Math.Clamp(_byEngine.Values.Max(), 0, 100);

            // Dedicated memory is a plain gauge rather than a rate, so its first collection is
            // already an answer and it is read whether or not the engine counter was ready. That
            // is what puts a figure in the video-memory column on the very first tick instead of
            // a second of nothing while the busyness counter finds its baseline.
            double held = 0;
            if (_memory.Read(processId, (_, value) => held += value)) _lastMemory = (long)held;

            return (_engine.HasRead ? _lastUsage : null, _lastMemory);
        }
    }

    /// <summary>Called when the model stops, so the next run measures from its own baseline.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _engine.Reset();
            _memory.Reset();
            _lastUsage = 0;
            _lastMemory = 0;
            _lastSample = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _engine.Dispose();
            _memory.Dispose();
        }
    }
}

/// <summary>
/// The card the engine would use, and how much memory it has.
///
/// llama.cpp will list its devices without loading anything — one short run of the same
/// executable that serves the model — and that is the only place the two facts the monitor
/// needs about the card are both stated: its name, which the accelerator badge reports, and its
/// total memory, which is what turns "5.7 GB of video memory" from a number into a share.
///
/// Read once per engine folder and kept. The engine does not change device between runs, and
/// initialising the driver to ask is not something to do on a timer.
/// </summary>
internal static class GpuDevice
{
    private static readonly Regex DeviceLine =
        new(@"^\s*\w+\d+:\s*(?<name>.+?)\s*\((?<total>[\d.]+)\s*MiB(?:,\s*(?<free>[\d.]+)\s*MiB\s+free)?",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Dictionary<string, (string? Name, long TotalBytes)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>What was found for this engine, or nothing while the answer is still on its way.</summary>
    public static (string? Name, long TotalBytes) Known(string serverPath)
    {
        lock (Gate) return Cache.TryGetValue(serverPath, out var found) ? found : (null, 0);
    }

    /// <summary>
    /// Asks the engine what card it sees, unless it has been asked before. Runs off the caller's
    /// thread: the answer is only wanted by a monitor that refreshes every second, and the model
    /// it belongs to takes far longer than this to load.
    /// </summary>
    public static void Probe(string serverPath)
    {
        lock (Gate)
        {
            if (Cache.ContainsKey(serverPath)) return;
            // Claimed before the run so a second start cannot set a second probe going.
            Cache[serverPath] = (null, 0);
        }
        _ = Task.Run(() =>
        {
            var found = Ask(serverPath);
            lock (Gate) Cache[serverPath] = (found.Name, found.TotalBytes);
        });
    }

    /// <summary>
    /// Asks right now and waits for the answer. For choosing how to run a model, where what is
    /// free on the card at this moment is the figure that matters and a remembered one will not do.
    /// </summary>
    public static (string? Name, long TotalBytes, long FreeBytes) ReadNow(string serverPath) => Ask(serverPath);

    private static (string? Name, long TotalBytes, long FreeBytes) Ask(string serverPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(serverPath)
            {
                WorkingDirectory = Path.GetDirectoryName(serverPath)!,
                ArgumentList = { "--list-devices" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return (null, 0, 0);
            // Both streams at once: reading one to its end while the other fills its pipe is how
            // a child process and its parent end up waiting on each other.
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(true); } catch { }
                process.WaitForExit(500);
                return (null, 0, 0);
            }
            if (!Task.WaitAll([output, errors], 1000)) return (null, 0, 0);
            var text = output.Result + errors.Result;

            // "  Vulkan0: NVIDIA GeForce RTX 2070 SUPER (8203 MiB, 7410 MiB free)"
            var match = DeviceLine.Match(text);
            if (!match.Success) return (null, 0, 0);
            var name = match.Groups["name"].Value.Trim();
            return (name.Length == 0 ? null : name, Mebibytes(match.Groups["total"].Value),
                match.Groups["free"].Success ? Mebibytes(match.Groups["free"].Value) : 0);
        }
        catch (Exception)
        {
            // A machine that will not say what card it has still reports how busy it is.
            return (null, 0, 0);
        }
    }

    private static long Mebibytes(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mebibytes)
            ? (long)(mebibytes * 1024 * 1024)
            : 0;
}

/// <summary>
/// How much memory the machine has, so the monitor can say what share of it the model took.
/// A number of bytes on its own answers "how much"; it does not answer "is that a lot".
/// </summary>
internal static class SystemMemory
{
    private static long _total = -1;

    /// <summary>Total physical memory in bytes, or 0 when the platform will not say.</summary>
    public static long TotalBytes
    {
        get
        {
            if (_total >= 0) return _total;
            _total = Read();
            return _total;
        }
    }

    /// <summary>
    /// Physical memory free right now, counting what the system would hand back from its file
    /// cache. Read fresh every time: it is the figure that says whether a model fits today.
    /// </summary>
    public static long AvailableBytes
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 0;
                }
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes) ? kilobytes * 1024 : 0;
                }
            }
            catch (Exception)
            {
                // Unknown is an answer the tuner knows how to take.
            }
            return 0;
        }
    }

    private static long Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status)) return (long)status.ullTotalPhys;
                return 0;
            }
            // Everywhere else the kernel publishes it as a line of text.
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes)) return kilobytes * 1024;
                break;
            }
        }
        catch (Exception)
        {
            // A machine that will not say how much memory it has still reports how much is used.
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

/// <summary>
/// The command-line switches the installed engine understands.
///
/// The engine updates itself from GitHub releases, so its options are not a contract: a switch a
/// newer build needs is one an older build refuses outright — llama-server exits on an argument
/// it does not know. The only reliable answer is the engine's own help text, read once per
/// executable and kept.
/// </summary>
internal static class EngineFlags
{
    private static readonly Regex Flag = new(@"(?<![\w-])(--?[A-Za-z][\w-]*)", RegexOptions.Compiled);
    private static readonly Dictionary<string, HashSet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static IReadOnlySet<string> Read(string serverPath)
    {
        lock (Gate)
            if (Cache.TryGetValue(serverPath, out var known)) return known;
        var flags = Ask(serverPath);
        lock (Gate) Cache[serverPath] = flags;
        return flags;
    }

    private static HashSet<string> Ask(string serverPath)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(serverPath)
            {
                WorkingDirectory = Path.GetDirectoryName(serverPath)!,
                ArgumentList = { "--help" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return flags;
            var errors = process.StandardError.ReadToEndAsync();
            var text = process.StandardOutput.ReadToEnd() + errors.GetAwaiter().GetResult();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(true); } catch { }
            }

            // Option lines start with the switch; descriptions and their continuations do not.
            foreach (var line in text.Split('\n'))
            {
                if (!line.TrimStart().StartsWith('-')) continue;
                foreach (Match match in Flag.Matches(line)) flags.Add(match.Groups[1].Value);
            }
        }
        catch (Exception)
        {
            // An engine that will not describe itself gets only the switches every build has.
        }
        return flags;
    }
}
