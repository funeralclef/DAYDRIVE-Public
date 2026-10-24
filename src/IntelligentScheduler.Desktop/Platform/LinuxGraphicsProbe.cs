using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

internal static class LinuxGraphicsProbe
{
    private const long MiB = 1024L * 1024;
    internal sealed record NvidiaCard(string Bus, string Name, long Total, long Free);

    public static IReadOnlyList<GpuAdapter> Read()
        => Read("/sys/class/drm", LinuxVulkanDevices.Read(), QueryNvidia());

    internal static IReadOnlyList<GpuAdapter> Read(string drmRoot, IReadOnlyList<LinuxVulkanDevice> vulkan, string? nvidiaOutput)
    {
        var result = new List<GpuAdapter>();
        var remaining = vulkan.ToList();
        var nvidia = ParseNvidia(nvidiaOutput);
        var usedNvidia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPci = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenModels = new HashSet<(uint Vendor, uint Device)>();
        foreach (var card in Directories(drmRoot))
        {
            var name = Path.GetFileName(card);
            if (!Regex.IsMatch(name, @"^card\d+$", RegexOptions.CultureInvariant)) continue;
            var device = Path.Combine(card, "device");
            var vendor = Hex(ReadText(Path.Combine(device, "vendor")));
            var model = Hex(ReadText(Path.Combine(device, "device")));
            if (vendor is null || model is null) continue; // Skip simpledrm and connector nodes.
            var bus = NormalizeBus(ReadText(Path.Combine(device, "uevent"))?.Split('\n')
                .FirstOrDefault(line => line.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal))?[14..]);
            if (bus is not null && !seenPci.Add(bus)) continue;
            var index = remaining.FindIndex(v => v.Vendor == vendor && v.Device == model);
            var vk = index >= 0 ? remaining[index] : null;
            if (index >= 0) remaining.RemoveAt(index);
            var nv = bus is not null ? nvidia.FirstOrDefault(n => n.Bus == bus) : null;
            if (nv is not null) usedNvidia.Add(nv.Bus);
            var total = nv?.Total is > 0 ? nv.Total : Bytes(Path.Combine(device, "mem_info_vram_total")) ?? vk?.LocalBytes ?? 0;
            var used = Bytes(Path.Combine(device, "mem_info_vram_used"));
            var free = nv?.Free ?? (used is not null && total > 0 ? Math.Clamp(total - used.Value, 0, total) : 0);
            var integrated = vk?.Integrated ?? (vendor is 0x8086 or 0x1002 && total < 1024 * MiB);
            var displayName = nv?.Name ?? vk?.Name ?? PciName(vendor.Value, model.Value)
                ?? $"{VendorName(vendor.Value)} GPU [{vendor:x4}:{model:x4}]";
            result.Add(new(displayName, total, free, integrated));
            seenModels.Add((vendor.Value, model.Value));
        }
        // Containers, headless drivers and devices without a DRM card may still be visible
        // through the vendor or Vulkan. A CPU/software Vulkan device is filtered upstream.
        foreach (var nv in nvidia.Where(n => !usedNvidia.Contains(n.Bus)))
            result.Add(new(nv.Name, nv.Total, nv.Free, false));
        foreach (var vk in remaining)
        {
            if (seenModels.Contains((vk.Vendor, vk.Device)) || vk.Vendor == 0x10de && nvidia.Count > 0) continue;
            if (result.Any(adapter => adapter.Name.Equals(vk.Name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new(vk.Name, vk.LocalBytes, 0, vk.Integrated));
        }
        return result;
    }

    internal static IReadOnlyList<NvidiaCard> ParseNvidia(string? output)
    {
        var result = new List<NvidiaCard>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            // Capture the last two CSV fields separately; a model name may contain a comma.
            var match = Regex.Match(line, @"^\s*(?<bus>[^,]+),\s*(?<name>.+),\s*(?<total>[^,]+),\s*(?<free>[^,]+)\s*$",
                RegexOptions.CultureInvariant);
            if (!match.Success || NormalizeBus(match.Groups["bus"].Value) is not { } bus) continue;
            var name = match.Groups["name"].Value.Trim().Trim('"');
            if (name.Length == 0) continue;
            var total = Mebibytes(match.Groups["total"].Value);
            result.Add(new(bus, name, total, Math.Clamp(Mebibytes(match.Groups["free"].Value), 0, total)));
        }
        return result;
    }

    internal static string? NormalizeBus(string? bus)
    {
        if (bus is null) return null;
        var match = Regex.Match(bus.Trim(), @"^(?<domain>[\da-fA-F]{4,8}):(?<bus>[\da-fA-F]{2}):(?<slot>[\da-fA-F]{2}\.[0-7])$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !uint.TryParse(match.Groups["domain"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var domain)) return null;
        return $"{domain:x4}:{match.Groups["bus"].Value}:{match.Groups["slot"].Value}".ToLowerInvariant();
    }

    private static long Mebibytes(string value)
        => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number is > 0 && number < (double)long.MaxValue / MiB ? (long)(number * MiB) : 0;
    private static uint? Hex(string? value)
        => uint.TryParse(value?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var number) ? number : null;
    private static long? Bytes(string path)
        => long.TryParse(ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static string VendorName(uint id) => id switch { 0x10de => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", _ => "PCI" };
    private static string[] Directories(string path)
    {
        try { return Directory.GetDirectories(path).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? PciName(uint vendor, uint device)
    {
        foreach (var database in new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" })
        {
            try
            {
                if (!File.Exists(database)) continue;
                var active = false;
                foreach (var line in File.ReadLines(database))
                {
                    if (line.Length < 5 || line[0] == '#') continue;
                    if (line[0] != '\t')
                    {
                        if (active) break;
                        active = Hex(line[..4]) == vendor;
                    }
                    else if (active && line.Length > 7 && line[1] != '\t' && Hex(line.Substring(1, 4)) == device)
                        return line[5..].Trim();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string? QueryNvidia()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi")
            {
                ArgumentList = { "--query-gpu=pci.bus_id,name,memory.total,memory.free", "--format=csv,noheader,nounits" },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            start.Environment["LC_ALL"] = "C";
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3500)) { process.Kill(entireProcessTree: true); process.WaitForExit(500); return null; }
            return process.ExitCode == 0 && output.Wait(500) && error.Wait(500) ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }
}
