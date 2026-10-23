using System.Text;
using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

/// <summary>
/// The tuner is arithmetic on stated facts, so each case is a machine someone might own and
/// the settings it should get. The first one is the machine this was written on.
/// </summary>
public sealed class LocalModelTuningTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    private static readonly ModelMemoryShape Qwen = LocalModelCatalog.Find("qwen35-9b").Shape;

    private static HardwareProfile Machine(long memory, long free, int cores, int performance, params GpuAdapter[] gpus)
        => new(memory, free, cores, performance, cores * 2, gpus);

    [Fact]
    public void AnEightGigabyteCardCarriesMostOfTheDefaultModel()
    {
        // An RTX 2070 SUPER with 7.2 GB of its 8 free: the 7.1 GB model does not fit whole with a
        // context beside it, and "every layer on the card" was the wrong answer for it.
        var tuning = LocalModelTuner.Recommend(
            Machine(32 * GiB, 20 * GiB, 8, 8, new GpuAdapter("NVIDIA GeForce RTX 2070 SUPER", 8203 * MiB, 7405 * MiB, false)),
            Qwen);

        Assert.Equal(LocalPlacement.Split, tuning.Placement);
        Assert.True(tuning.UseGpu);
        Assert.InRange(tuning.GpuShare, 0.8, 0.99);
        Assert.Equal(16_384, tuning.ContextTokens);
        Assert.Equal(8, tuning.Threads);
        Assert.False(tuning.IsMemoryTight);
    }

    [Fact]
    public void ABigCardTakesTheWholeModelWithALongContext()
    {
        var tuning = LocalModelTuner.Recommend(
            Machine(64 * GiB, 40 * GiB, 16, 16, new GpuAdapter("RTX 4090", 24 * GiB, 22 * GiB, false)), Qwen);

        Assert.Equal(LocalPlacement.Gpu, tuning.Placement);
        Assert.Equal(1, tuning.GpuShare);
        Assert.Equal(32_768, tuning.ContextTokens);
        Assert.Equal(4_000, tuning.MaxTokens);
        Assert.Equal(32, tuning.HistoryTurns);
        Assert.Equal(5, tuning.TimeoutMinutes);
        // On the card the processor has little to do; reading the prompt still gets every core.
        Assert.Equal(8, tuning.Threads);
        Assert.Equal(16, tuning.BatchThreads);
    }

    [Fact]
    public void WithoutACardAHybridProcessorGeneratesOnItsFastCoresOnly()
    {
        // Six performance and eight efficiency cores, twenty threads, graphics built in.
        var tuning = LocalModelTuner.Recommend(
            new HardwareProfile(16 * GiB, 10 * GiB, 14, 6, 20, [new GpuAdapter("Intel(R) UHD Graphics", 128 * MiB, 0, true)]),
            Qwen);

        Assert.Equal(LocalPlacement.Cpu, tuning.Placement);
        Assert.False(tuning.UseGpu);
        Assert.Equal(6, tuning.Threads);
        Assert.Equal(14, tuning.BatchThreads);
        // Fewer than eight fast cores read a long prompt slowly, so the context stays short.
        Assert.Equal(8_192, tuning.ContextTokens);
        Assert.Equal(2_000, tuning.MaxTokens);
        Assert.Equal(15, tuning.TimeoutMinutes);
    }

    [Fact]
    public void AMachineShortOfMemoryIsToldSo()
    {
        var tuning = LocalModelTuner.Recommend(Machine(8 * GiB, 3 * GiB, 4, 4), Qwen);

        Assert.Equal(LocalPlacement.Cpu, tuning.Placement);
        Assert.True(tuning.IsMemoryTight);
        Assert.True(tuning.RequiredBytes > tuning.AvailableBytes);
        Assert.Equal(8_192, tuning.ContextTokens);
        // The model is handed back sooner when it is crowding the machine.
        Assert.Equal(5, tuning.IdleMinutes);
    }

    [Fact]
    public void ACardTooSmallToHelpIsLeftAlone()
    {
        var tuning = LocalModelTuner.Recommend(
            Machine(16 * GiB, 12 * GiB, 8, 8, new GpuAdapter("GeForce GTX 1050", 2 * GiB, 1800 * MiB, false)), Qwen);

        Assert.Equal(LocalPlacement.Cpu, tuning.Placement);
        Assert.Equal(0, tuning.GpuShare);
    }

    [Fact]
    public void TheCardCanBeRuledOut()
    {
        var tuning = LocalModelTuner.Recommend(
            Machine(32 * GiB, 20 * GiB, 8, 8, new GpuAdapter("RTX 4090", 24 * GiB, 22 * GiB, false)), Qwen, gpuAllowed: false);

        Assert.Equal(LocalPlacement.Cpu, tuning.Placement);
        Assert.False(tuning.UseGpu);
    }

    [Fact]
    public void AHybridModelGetsMoreContextFromTheSameCardThanAConventionalOne()
    {
        var machine = Machine(32 * GiB, 20 * GiB, 8, 8, new GpuAdapter("RTX 3060", 12 * GiB, 11 * GiB, false));
        var hybrid = LocalModelTuner.Recommend(machine, Qwen);
        var conventional = LocalModelTuner.Recommend(machine, ModelMemoryShape.Conventional(Qwen.WeightsBytes));

        Assert.Equal(LocalPlacement.Gpu, hybrid.Placement);
        Assert.Equal(32_768, hybrid.ContextTokens);
        Assert.True(conventional.ContextTokens < hybrid.ContextTokens);
    }

    [Fact]
    public void AnUnknownMachineStillGetsWorkableSettings()
    {
        var tuning = LocalModelTuner.Recommend(HardwareProfile.Unknown, Qwen);

        Assert.Equal(LocalPlacement.Cpu, tuning.Placement);
        Assert.True(tuning.Threads >= 1);
        Assert.True(tuning.ContextTokens >= LocalModelTuner.SmallestContext);
    }

    // ---------------------------------------------------------------- GGUF

    [Fact]
    public void AHybridsHeaderGivesAQuarterOfTheConventionalCache()
    {
        using var file = Gguf("qwen35",
            ("block_count", 32u), ("context_length", 262_144u), ("embedding_length", 4096u),
            ("attention.head_count", 16u), ("attention.head_count_kv", 4u),
            ("attention.key_length", 256u), ("attention.value_length", 256u), ("full_attention_interval", 4u));

        var shape = GgufMetadata.ReadShape(file, file.Length);

        Assert.NotNull(shape);
        Assert.Equal(32, shape.Layers);
        Assert.Equal(32_768, shape.KvBytesPerToken);
        Assert.Equal(262_144, shape.TrainedContextTokens);
        // The catalogue's figure for the model agrees with what its own file says.
        Assert.Equal(Qwen.KvBytesPerToken, shape.KvBytesPerToken);
    }

    [Fact]
    public void PerBlockHeadCountsAreAddedUp()
    {
        // A Mamba hybrid lists no heads for the blocks that keep no cache.
        using var file = Gguf("granitehybrid",
            ("block_count", 4u), ("attention.head_count", 32u), ("embedding_length", 4096u),
            ("attention.head_count_kv", new[] { 0, 8, 0, 8 }));

        var shape = GgufMetadata.ReadShape(file, file.Length);

        Assert.NotNull(shape);
        Assert.Equal(16L * (128 + 128) * 2, shape.KvBytesPerToken);
    }

    [Fact]
    public void AFileThatIsNotGgufGivesNothing()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("definitely not a model file"));
        Assert.Null(GgufMetadata.ReadShape(stream, stream.Length));
    }

    /// <summary>A GGUF header: the architecture, its keys, then a vocabulary the reader must step over.</summary>
    private static MemoryStream Gguf(string architecture, params (string Key, object Value)[] entries)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46554747u);
            writer.Write(3u);
            writer.Write(0UL);
            writer.Write((ulong)(entries.Length + 2));
            WriteString(writer, "general.architecture");
            writer.Write(8u);
            WriteString(writer, architecture);
            foreach (var (key, value) in entries)
            {
                WriteString(writer, $"{architecture}.{key}");
                switch (value)
                {
                    case uint number:
                        writer.Write(4u);
                        writer.Write(number);
                        break;
                    case int[] numbers:
                        writer.Write(9u);
                        writer.Write(5u);
                        writer.Write((ulong)numbers.Length);
                        foreach (var number in numbers) writer.Write(number);
                        break;
                }
            }
            WriteString(writer, "tokenizer.ggml.tokens");
            writer.Write(9u);
            writer.Write(8u);
            writer.Write(3UL);
            foreach (var token in new[] { "a", "bb", "ccc" }) WriteString(writer, token);
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
