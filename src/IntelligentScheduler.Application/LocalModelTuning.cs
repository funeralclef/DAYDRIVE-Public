namespace IntelligentScheduler.Application;

/// <summary>A graphics adapter as the probe found it.</summary>
public sealed record GpuAdapter(
    string Name,
    /// <summary>Memory on the card itself; small or 0 for one that borrows system memory.</summary>
    long DedicatedBytes,
    /// <summary>What is free on it right now, or 0 when nothing could say.</summary>
    long FreeBytes,
    bool IsIntegrated);

/// <summary>The machine, as far as choosing how to run a model is concerned.</summary>
public sealed record HardwareProfile(
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    int PhysicalCores,
    /// <summary>Cores of the fastest class; equal to <see cref="PhysicalCores"/> without efficiency cores.</summary>
    int PerformanceCores,
    int LogicalProcessors,
    IReadOnlyList<GpuAdapter> Gpus)
{
    /// <summary>What is assumed about a machine that would not say anything about itself.</summary>
    public static HardwareProfile Unknown { get; } = new(0, 0, 0, 0, Math.Max(1, Environment.ProcessorCount), []);

    public bool IsKnown => TotalMemoryBytes > 0;

    /// <summary>The adapter worth running a model on: the separate card with the most memory.</summary>
    public GpuAdapter? BestGpu => Gpus
        .Where(gpu => !gpu.IsIntegrated && gpu.DedicatedBytes > 0)
        .OrderByDescending(gpu => gpu.DedicatedBytes)
        .FirstOrDefault();

    public int EfficiencyCores => Math.Max(0, PhysicalCores - PerformanceCores);
}

/// <summary>What a model needs in memory: its weights, and what every token of context adds.</summary>
public sealed record ModelMemoryShape(
    long WeightsBytes,
    int Layers,
    /// <summary>Key and value cache for one token, at the engine's default 16-bit precision.</summary>
    long KvBytesPerToken,
    int TrainedContextTokens)
{
    /// <summary>
    /// A conventional 8–9B transformer: 36 blocks, 8 KV heads of 128. It overstates what the
    /// hybrid and sliding-window designs keep, so a context chosen from it errs toward fitting.
    /// Replaced by the real figures as soon as the model's own file can be read.
    /// </summary>
    public static ModelMemoryShape Conventional(long weightsBytes) => new(weightsBytes, 36, 36 * 8 * (128 + 128) * 2, 32_768);

    public long ContextBytes(int tokens) => KvBytesPerToken * Math.Max(0, tokens);
}

public enum LocalPlacement
{
    /// <summary>The whole model on the graphics card: the fast case.</summary>
    Gpu,
    /// <summary>As much as fits on the card, the rest on the processor.</summary>
    Split,
    /// <summary>No card worth using, or the user said no.</summary>
    Cpu
}

/// <summary>Everything the tuner decided, and the arithmetic it decided it from.</summary>
public sealed record LocalTuning(
    LocalPlacement Placement,
    bool UseGpu,
    /// <summary>Share of the weights expected on the card, 0–1.</summary>
    double GpuShare,
    int ContextTokens,
    int Threads,
    /// <summary>Threads for reading the prompt, which unlike generation scales with every core.</summary>
    int BatchThreads,
    int MaxTokens,
    int HistoryTurns,
    int TimeoutMinutes,
    int IdleMinutes,
    /// <summary>Memory the model needs in all: weights, projector and context.</summary>
    long RequiredBytes,
    /// <summary>Memory the model can have without crowding out everything else.</summary>
    long AvailableBytes,
    /// <summary>True when the model does not comfortably fit and will be slow or crowd the machine.</summary>
    bool IsMemoryTight);

/// <summary>
/// Chooses how to run a model on a particular machine.
///
/// Every number here used to be one fixed default for every computer — an 8K context and
/// "every layer on the card" whether the card had four gigabytes or twenty-four, and a thread
/// count left to llama.cpp, which counts hyper-threads and efficiency cores as if they were the
/// same as the fast ones. The tuner works from what the machine actually has and what the model
/// actually needs, and says so in terms a settings page can show.
///
/// It is arithmetic on stated facts and nothing else, so it is tested rather than trusted. The
/// engine gets the last word on the split: a current llama.cpp fits the layers to what is free
/// on the card at load time, which no estimate made beforehand can beat.
/// </summary>
public static class LocalModelTuner
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    /// <summary>Kept free on the card for compute buffers — the margin llama.cpp's own fitting leaves.</summary>
    private const long GpuReserve = 1024 * MiB;

    /// <summary>What the desktop and other programs are assumed to hold when the card will not say.</summary>
    private const long DesktopVideoMemory = 700 * MiB;

    /// <summary>Beneath this a card holds too little of an 8–9B model to be worth the transfers.</summary>
    private const long SmallestUsefulCard = 3 * GiB;

    /// <summary>A card taking less than this share of the model is slower than leaving it all on the processor.</summary>
    private const double SmallestUsefulShare = 0.15;

    /// <summary>The system prompt and tool catalogue come to about this before the conversation starts.</summary>
    private const int PromptPreambleTokens = 5_000;

    /// <summary>An earlier message, on average, with the tool results that came with it.</summary>
    private const int TokensPerTurn = 400;

    /// <summary>Contexts offered, smallest first. Below the first the prompt alone would not fit.</summary>
    private static readonly int[] ContextSteps = [8_192, 12_288, 16_384, 24_576, 32_768];

    public static int SmallestContext => ContextSteps[0];

    /// <param name="extraBytes">Anything loaded beside the weights, such as the image projector.</param>
    public static LocalTuning Recommend(HardwareProfile hardware, ModelMemoryShape model,
        bool gpuAllowed = true, long extraBytes = 0)
    {
        var weights = Math.Max(0, model.WeightsBytes) + Math.Max(0, extraBytes);
        var ramBudget = RamBudget(hardware);
        var gpu = gpuAllowed ? hardware.BestGpu : null;
        if (gpu is not null && gpu.DedicatedBytes < SmallestUsefulCard) gpu = null;

        LocalPlacement placement;
        double share;
        int context;
        long available;

        if (gpu is not null && LargestContext(model, GpuBudget(gpu) - weights, 32_768) is var onCard and > 0)
        {
            placement = LocalPlacement.Gpu;
            share = 1;
            context = onCard;
            available = GpuBudget(gpu);
        }
        else if (gpu is not null)
        {
            // Part of it on the card. The context is paid for before the layers: the cache of
            // every layer that lands on the card lives there too.
            context = LargestContext(model, ramBudget - weights, 16_384) is var roomy and > 0 ? roomy : SmallestContext;
            share = Math.Clamp((double)(GpuBudget(gpu) - model.ContextBytes(context)) / Math.Max(1, weights), 0, 1);
            placement = share >= SmallestUsefulShare ? LocalPlacement.Split : LocalPlacement.Cpu;
            if (placement == LocalPlacement.Cpu) share = 0;
            available = ramBudget + (placement == LocalPlacement.Split ? GpuBudget(gpu) : 0);
        }
        else
        {
            placement = LocalPlacement.Cpu;
            share = 0;
            // Reading a long prompt on the processor is slow, so a small processor is not
            // handed a long context just because the memory would hold it.
            var cap = PerformanceCores(hardware) >= 8 ? 16_384 : SmallestContext;
            context = LargestContext(model, ramBudget - weights, cap) is var fits and > 0 ? fits : SmallestContext;
            available = ramBudget;
        }

        var required = weights + model.ContextBytes(context);
        var threads = placement == LocalPlacement.Gpu
            ? Math.Clamp(PerformanceCores(hardware), 1, 8)
            : Math.Clamp(PerformanceCores(hardware), 1, 16);
        var batchThreads = Math.Clamp(PhysicalCores(hardware), threads, 32);
        var maxTokens = Math.Min(placement switch
        {
            LocalPlacement.Gpu => 4_000,
            LocalPlacement.Split => 3_000,
            _ => 2_000
        }, context / 3);
        var history = Math.Clamp((context - PromptPreambleTokens - maxTokens) / TokensPerTurn / 2 * 2, 4, 32);
        var timeout = placement switch
        {
            LocalPlacement.Gpu => 5,
            LocalPlacement.Split => 10,
            _ => 15
        };
        var tight = required > available;
        // A model holding a large share of the machine's memory is given back sooner when idle.
        var heavy = placement != LocalPlacement.Gpu && hardware.TotalMemoryBytes > 0 &&
                    weights > hardware.TotalMemoryBytes * 0.4;
        var idle = tight || heavy ? 5 : 10;

        return new LocalTuning(placement, placement != LocalPlacement.Cpu, share, context, threads, batchThreads,
            maxTokens, history, timeout, idle, required, available, tight);
    }

    /// <summary>The largest offered context whose cache fits in <paramref name="budget"/>, or 0 when none does.</summary>
    private static int LargestContext(ModelMemoryShape model, long budget, int cap)
    {
        var limit = Math.Min(cap, model.TrainedContextTokens > 0 ? model.TrainedContextTokens : cap);
        var best = 0;
        foreach (var step in ContextSteps)
            if (step <= limit && model.ContextBytes(step) <= budget) best = step;
        return best;
    }

    private static long GpuBudget(GpuAdapter gpu)
        => (gpu.FreeBytes > 0 ? gpu.FreeBytes : gpu.DedicatedBytes - DesktopVideoMemory) - GpuReserve;

    /// <summary>
    /// Memory a model may take: what is free now, but never so much that the system and the
    /// programs around it are left with less than a sixth of the machine or two gigabytes.
    /// </summary>
    private static long RamBudget(HardwareProfile hardware)
    {
        if (!hardware.IsKnown) return 8 * GiB;
        var reserve = Math.Max(2 * GiB, hardware.TotalMemoryBytes / 6);
        var ceiling = hardware.TotalMemoryBytes - reserve;
        return hardware.AvailableMemoryBytes > 0 ? Math.Min(ceiling, hardware.AvailableMemoryBytes) : ceiling;
    }

    private static int PhysicalCores(HardwareProfile hardware)
        => hardware.PhysicalCores > 0 ? hardware.PhysicalCores : Math.Max(1, hardware.LogicalProcessors / 2);

    private static int PerformanceCores(HardwareProfile hardware)
        => hardware.PerformanceCores > 0 ? hardware.PerformanceCores : PhysicalCores(hardware);
}
