namespace IntelligentScheduler.Application;

/// <summary>
/// The one precision every catalogue model comes in. Q5_K_M keeps close to all of a model's
/// quality at a size an ordinary machine can hold. The lighter and heavier variants the
/// catalogue used to offer beside it were a choice few people could make well, and the machine
/// is a better judge of what fits than a drop-down is: see <see cref="LocalModelTuner"/>.
/// </summary>
public sealed record LocalModelQuant(
    /// <summary>The llama.cpp quantisation tag, e.g. <c>Q5_K_M</c>.</summary>
    string Tag,
    string ApproximateSizeLabel,
    long ApproximateBytes);

public sealed record LocalModelDescriptor(
    string Id,
    string DisplayName,
    string ParameterLabel,
    string RoleKey,
    string HuggingFaceRepository,
    /// <summary>File name with <c>{quant}</c> standing in for the quantisation tag.</summary>
    string FileTemplate,
    /// <summary>Null when the repository ships no multimodal projector.</summary>
    string? ProjectorFileName,
    LocalModelQuant Quant,
    /// <summary>What the model needs in memory, until the downloaded file can say for itself.</summary>
    ModelMemoryShape Shape,
    bool IsRecommended = false,
    /// <summary>Shown behind an "advanced" marker rather than offered as a first choice.</summary>
    bool IsExperimental = false)
{
    public bool SupportsVision => ProjectorFileName is not null;

    public string FileName => FileTemplate.Replace("{quant}", Quant.Tag, StringComparison.Ordinal);

    public Uri ModelDownloadUri => HuggingFaceFile(FileName);

    public Uri? ProjectorDownloadUri => ProjectorFileName is null ? null : HuggingFaceFile(ProjectorFileName);

    private Uri HuggingFaceFile(string fileName)
        => new($"https://huggingface.co/{HuggingFaceRepository}/resolve/main/{fileName}?download=true");
}

/// <summary>Which catalogue entry the user is running, or a GGUF of their own.</summary>
public readonly record struct LocalModelSelection(string ModelId)
{
    public bool IsCustom => LocalModelCatalog.IsCustom(ModelId);

    public override string ToString() => IsCustom ? LocalModelCatalog.CustomModelId : ModelId;
}

public static class LocalModelCatalog
{
    public const string DefaultModelId = "qwen35-9b";

    /// <summary>Reserved id for a GGUF the user supplied from disk.</summary>
    public const string CustomModelId = "custom";

    /// <summary>The only precision on offer; see <see cref="LocalModelQuant"/>.</summary>
    public const string QuantTag = "Q5_K_M";

    /// <summary>
    /// Folders earlier releases downloaded the other precisions into. Nothing runs from them now;
    /// the settings page offers to reclaim the space they hold.
    /// </summary>
    public static IReadOnlyList<string> RetiredQuantTags { get; } = ["Q4_K_M", "Q6_K"];

    public static bool IsCustom(string? id) => CustomModelId.Equals(id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Three models, one precision each. Every repository and file name here was checked
    /// against the Hugging Face API; adding an entry means checking the new one too.
    /// </summary>
    public static IReadOnlyList<LocalModelDescriptor> Models { get; } =
    [
        new("qwen35-9b", "Qwen3.5 9B", "9B", "LocalModelRoleGeneral",
            "unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-{quant}.gguf", "mmproj-F16.gguf",
            new LocalModelQuant(QuantTag, "≈ 7.1 GB", 7_110_000_000),
            // Read off the GGUF header: 32 blocks, attention in one of every four, 4 KV heads of
            // 256 + 256 — a hybrid that keeps a quarter of the cache a conventional 9B would.
            new ModelMemoryShape(7_110_000_000, 32, 8 * 4 * (256 + 256) * 2, 262_144),
            IsRecommended: true),

        new("ministral-8b-reasoning", "Ministral 3 8B Reasoning", "8B", "LocalModelRoleReasoning",
            "unsloth/Ministral-3-8B-Reasoning-2512-GGUF", "Ministral-3-8B-Reasoning-2512-{quant}.gguf", "mmproj-F16.gguf",
            new LocalModelQuant(QuantTag, "≈ 6.1 GB", 6_060_000_000),
            ModelMemoryShape.Conventional(6_060_000_000)),

        new("granite-4-8b", "Granite 4.1 8B", "8B", "LocalModelRoleToolUse",
            "unsloth/granite-4.1-8b-GGUF", "granite-4.1-8b-{quant}.gguf", null,
            new LocalModelQuant(QuantTag, "≈ 6.3 GB", 6_253_884_800),
            ModelMemoryShape.Conventional(6_253_884_800),
            IsExperimental: true)
    ];

    /// <summary>Placeholder descriptor for a user-supplied file, so the UI has something to show.</summary>
    public static LocalModelDescriptor Custom { get; } = new(
        CustomModelId, "Custom GGUF", "—", "LocalModelRoleCustom",
        string.Empty, string.Empty, null,
        new LocalModelQuant("—", "—", 0),
        ModelMemoryShape.Conventional(0));

    public static LocalModelDescriptor Find(string? id)
    {
        if (IsCustom(id)) return Custom;
        if (string.IsNullOrWhiteSpace(id)) return Models.Single(model => model.Id == DefaultModelId);

        var direct = Models.FirstOrDefault(model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;

        // Ids stored by earlier releases, so an upgrade does not silently reset the choice.
        return id.ToLowerInvariant() switch
        {
            "lite" or "balanced" or "smart" => Models.Single(model => model.Id == DefaultModelId),
            var value when value.Contains("qwen") => Models.Single(model => model.Id == "qwen35-9b"),
            var value when value.Contains("ministral") => Models.Single(model => model.Id == "ministral-8b-reasoning"),
            var value when value.Contains("granite") => Models.Single(model => model.Id == "granite-4-8b"),
            _ => Models.Single(model => model.Id == DefaultModelId)
        };
    }
}

public enum LocalModelRuntimeState
{
    Stopped,
    DownloadingEngine,
    ExtractingEngine,
    DownloadingModel,
    DownloadingProjector,
    Starting,
    Running,
    Sleeping,
    Error
}

public sealed record LocalModelRuntimeStatus(
    string ModelId,
    LocalModelRuntimeState State,
    bool EngineInstalled,
    bool ModelInstalled,
    bool ProjectorInstalled,
    double Progress = 0,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    string? Error = null);

public sealed class LocalModelStatusChangedEventArgs(LocalModelRuntimeStatus status) : EventArgs
{
    public LocalModelRuntimeStatus Status { get; } = status;
}

public sealed record LocalModelResourceMetrics(
    long MemoryBytes,
    double CpuPercent,
    string ModelName,
    TimeSpan Uptime,
    bool IsRunning,
    /// <summary>Layers the engine actually handed to the graphics card, 0 when it is all CPU.</summary>
    int GpuLayers = 0,
    /// <summary>Layers the model has in total, so a partial offload can be told from a full one.</summary>
    int TotalLayers = 0,
    /// <summary>Name the engine printed for the device it is using, when it named one.</summary>
    string? Accelerator = null,
    /// <summary>Video memory the process is holding on the card right now, 0 when unknown.</summary>
    long VideoMemoryBytes = 0,
    /// <summary>Video memory the card has in total, so the figure above can be given a scale.</summary>
    long VideoMemoryTotalBytes = 0,
    /// <summary>
    /// The share of the graphics card the engine is using right now, or null where the platform
    /// keeps no such figure. Distinct from the layer count above, which says where the model was
    /// put; this says whether the card is actually working.
    /// </summary>
    double? GpuPercent = null,
    /// <summary>Physical memory in the machine, so the figure above can be given a scale. 0 when unknown.</summary>
    long TotalMemoryBytes = 0)
{
    /// <summary>
    /// Whether the model is really on the card. The layer count comes out of the engine's log
    /// and current builds no longer print it, so holding video memory counts too: a process
    /// with gigabytes of dedicated allocation is not running on the processor.
    /// </summary>
    public bool IsGpuAccelerated => GpuLayers > 0 || VideoMemoryBytes > 0;
}

/// <summary>Knobs that change how llama-server itself is launched.</summary>
public sealed record LocalRuntimeOptions(
    /// <summary>Context window in tokens. Smaller is faster and lighter on RAM.</summary>
    int ContextTokens = 8192,
    /// <summary>0 lets llama.cpp decide; otherwise the number of threads to pin.</summary>
    int Threads = 0,
    bool UseProjector = false,
    int IdleMinutes = 10,
    /// <summary>Absolute path to a user-supplied GGUF; only read for the custom entry.</summary>
    string? CustomModelPath = null,
    string? CustomProjectorPath = null,
    /// <summary>
    /// Whether the model may run on the graphics card. On means a GPU build of the engine is
    /// installed and the engine fits as much of the model as the card has room for; off keeps
    /// the CPU build, for a machine where the driver misbehaves or the card is wanted elsewhere.
    /// </summary>
    bool UseGpu = true);

public interface ILocalModelRuntime : IDisposable
{
    IReadOnlyList<LocalModelDescriptor> Models { get; }
    LocalModelRuntimeStatus Status { get; }
    event EventHandler<LocalModelStatusChangedEventArgs>? StatusChanged;

    LocalModelRuntimeStatus GetStatus(LocalModelSelection selection, string? customModelPath = null);
    LocalModelResourceMetrics? GetResourceMetrics();
    Task RefreshAsync(LocalModelSelection selection, CancellationToken cancellationToken = default);
    /// <summary>
    /// Fetches the engine and the model. <paramref name="useGpu"/> decides which build of the
    /// engine comes down, so the first download already matches the switch in settings instead
    /// of fetching one build now and the other one at the first run.
    /// </summary>
    Task DownloadAsync(LocalModelSelection selection, bool includeProjector, bool useGpu = true,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(LocalModelSelection selection, CancellationToken cancellationToken = default);
    Task<Uri> EnsureReadyAsync(LocalModelSelection selection, LocalRuntimeOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(bool sleeping = false, CancellationToken cancellationToken = default);
    void NotifyActivity(int idleMinutes);

    /// <summary>The GGUF a selection runs from, when it is on disk; null otherwise.</summary>
    string? ModelFilePath(LocalModelSelection selection);

    /// <summary>
    /// The machine the engine will run on. Slow the first time — it asks the operating system
    /// and, when a GPU build is installed, the engine itself — so call it off the UI thread.
    /// </summary>
    HardwareProfile ReadHardware();

    /// <summary>Space held by downloads in precisions the catalogue no longer offers.</summary>
    long RetiredDownloadBytes();

    /// <summary>Deletes those downloads and returns how much was freed.</summary>
    Task<long> DeleteRetiredDownloadsAsync(CancellationToken cancellationToken = default);
}
