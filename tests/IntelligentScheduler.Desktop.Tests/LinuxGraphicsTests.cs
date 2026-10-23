using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Platform;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class LinuxGraphicsTests : IDisposable
{
    private const long MiB = 1024L * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daydrive graphics " + Guid.NewGuid());

    private void Card(string name, string vendor, string model, string bus, long? total = null, long? used = null)
    {
        var path = Path.Combine(_root, name, "device");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "vendor"), vendor);
        File.WriteAllText(Path.Combine(path, "device"), model);
        File.WriteAllText(Path.Combine(path, "uevent"), "PCI_SLOT_NAME=" + bus + "\n");
        if (total is not null) File.WriteAllText(Path.Combine(path, "mem_info_vram_total"), total.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (used is not null) File.WriteAllText(Path.Combine(path, "mem_info_vram_used"), used.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void HybridLaptopFindsBothCardsAndSelectsDedicatedNvidiaWithoutAModelEngine()
    {
        Card("card0", "0x10de", "0x25a0", "0000:01:00.0");
        Card("card1", "0x1002", "0x164c", "0000:05:00.0", 512 * MiB, 100 * MiB);
        var found = LinuxGraphicsProbe.Read(_root, [
            new(0x1002, 0x164c, "AMD Radeon Graphics", 0, true),
            new(0x10de, 0x25a0, "NVIDIA GeForce RTX 3050 Ti Laptop GPU", 4096 * MiB, false)],
            "00000000:01:00.0, NVIDIA GeForce RTX 3050 Ti Laptop GPU, 4096, 3797\n");
        Assert.Equal(2, found.Count);
        var nvidia = Assert.Single(found, card => !card.IsIntegrated);
        Assert.Equal(4096 * MiB, nvidia.DedicatedBytes);
        Assert.Equal(3797 * MiB, nvidia.FreeBytes);
        var amd = Assert.Single(found, card => card.IsIntegrated);
        Assert.Equal(512 * MiB, amd.DedicatedBytes);
        Assert.Equal(412 * MiB, amd.FreeBytes);
        Assert.Same(nvidia, new HardwareProfile(16 * 1024 * MiB, 8 * 1024 * MiB, 8, 8, 16, found).BestGpu);
    }

    [Fact]
    public void VulkanIdentifiesDiscreteIntelAndDoesNotCountSharedRamAsDedicatedVram()
    {
        var found = LinuxGraphicsProbe.Read(_root, [
            new(0x8086, 1, "Intel Arc", 8192 * MiB, false),
            new(0x8086, 2, "Intel Graphics", 0, true)], null);
        Assert.Equal(2, found.Count);
        Assert.Equal(8192 * MiB, Assert.Single(found, card => !card.IsIntegrated).DedicatedBytes);
        Assert.Equal(0, Assert.Single(found, card => card.IsIntegrated).DedicatedBytes);
    }

    [Fact]
    public void SysfsFindsAnAdapterWhenVulkanAndVendorToolsAreUnavailable()
    {
        Card("card0", "0xfffe", "0xfffe", "0000:02:00.0", 4096 * MiB, 300 * MiB);
        var card = Assert.Single(LinuxGraphicsProbe.Read(_root, [], null));
        Assert.Contains("fffe:fffe", card.Name);
        Assert.Equal(4096 * MiB, card.DedicatedBytes);
        Assert.Equal(3796 * MiB, card.FreeBytes);
        Assert.False(card.IsIntegrated);
    }

    [Fact]
    public void RefreshReadsNewCardsAndCurrentVideoMemory()
    {
        Assert.Empty(LinuxGraphicsProbe.Read(_root, [], null));
        Card("card0", "0xfffe", "0xfffe", "0000:02:00.0", 4096 * MiB, 300 * MiB);
        Assert.Equal(3796 * MiB, Assert.Single(LinuxGraphicsProbe.Read(_root, [], null)).FreeBytes);
        Card("card0", "0xfffe", "0xfffe", "0000:02:00.0", 4096 * MiB, 900 * MiB);
        Assert.Equal(3196 * MiB, Assert.Single(LinuxGraphicsProbe.Read(_root, [], null)).FreeBytes);
    }

    [Fact]
    public void ConnectorsAndAliasesDoNotDuplicateTheSamePciDevice()
    {
        Card("card0", "0x10de", "0x25a0", "0000:01:00.0");
        Card("card1", "0x10de", "0x25a0", "0000:01:00.0");
        Card("card0-HDMI-A-1", "0x10de", "0x25a0", "0000:01:00.0");
        var cards = LinuxGraphicsProbe.Read(_root, [new(0x10de, 0x25a0, "NVIDIA GPU", 4096 * MiB, false)],
            "00000000:01:00.0, NVIDIA GPU, 4096, 3900");
        Assert.Single(cards);
    }

    [Fact]
    public void TwoIdenticalPhysicalCardsRemainSeparate()
    {
        Card("card0", "0x10de", "0x25a0", "0000:01:00.0");
        Card("card1", "0x10de", "0x25a0", "0000:02:00.0");
        var cards = LinuxGraphicsProbe.Read(_root, [new(0x10de, 0x25a0, "NVIDIA GPU", 4096 * MiB, false),
            new(0x10de, 0x25a0, "NVIDIA GPU", 4096 * MiB, false)],
            "00000000:01:00.0, NVIDIA GPU, 4096, 3900\n00000000:02:00.0, NVIDIA GPU, 4096, 3000");
        Assert.Equal(2, cards.Count);
        Assert.Equal(new[] { 3900 * MiB, 3000 * MiB }, cards.Select(card => card.FreeBytes));
    }

    [Fact]
    public void NvidiaWorksWithoutDrmAndVulkanAndRejectsMalformedOutput()
    {
        var cards = LinuxGraphicsProbe.Read(_root, [], "driver failure\n00000000:01:00.0, NVIDIA GPU, 4096, 3500\n");
        Assert.Equal(4096 * MiB, Assert.Single(cards).DedicatedBytes);
        Assert.Empty(LinuxGraphicsProbe.ParseNvidia("invalid, incomplete\nNVIDIA-SMI has failed"));
    }

    [Theory]
    [InlineData("00000000:AF:01.0", "0000:af:01.0")]
    [InlineData("0001:02:03.1", "0001:02:03.1")]
    [InlineData("not-a-bus", null)]
    public void NvidiaAndSysfsBusAddressesMatch(string raw, string? expected)
        => Assert.Equal(expected, LinuxGraphicsProbe.NormalizeBus(raw));

    [Fact]
    public void InstalledEngineCanReportItsDeviceOnStandardError()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "test llama-server");
        File.WriteAllText(path, "#!/bin/sh\nprintf '  Vulkan0: NVIDIA GeForce RTX 3050 Ti Laptop GPU (4096 MiB, 3797 MiB free)\\n' >&2\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var device = GpuDevice.ReadNow(path);
        Assert.Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", device.Name);
        Assert.Equal(4096 * MiB, device.TotalBytes);
        Assert.Equal(3797 * MiB, device.FreeBytes);
    }

    [Fact]
    public void UnknownMemoryDoesNotDiscardTheCardAndInvalidFreeMemoryIsClamped()
    {
        var unknown = Assert.Single(LinuxGraphicsProbe.ParseNvidia("0000:01:00.0, NVIDIA GPU, [N/A], [N/A]"));
        Assert.Equal("NVIDIA GPU", unknown.Name);
        Assert.Equal(0, unknown.Total);
        var known = Assert.Single(LinuxGraphicsProbe.ParseNvidia("0000:01:00.0, \"NVIDIA GPU, test\", 4096, 5000"));
        Assert.Equal("NVIDIA GPU, test", known.Name);
        Assert.Equal(known.Total, known.Free);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
