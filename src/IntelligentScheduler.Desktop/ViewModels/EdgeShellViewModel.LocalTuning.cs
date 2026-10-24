using System.Collections.Concurrent;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// The local model's settings, kept apart from the API's.
///
/// A hosted API is configured by what goes into each request, and nothing else about it is this
/// machine's business. The local model is configured mostly by the machine: how much of it the
/// graphics card can hold, how long a context the memory leaves room for, how many cores are
/// quick enough to generate on. Those used to be fixed defaults under the same heading as the
/// API's temperature — which is how an 8 GB card came to be told to hold every layer of a 7 GB
/// model, with nothing left for the context. The machine is read now, the numbers are worked
/// out from it, and the page says both what was found and what was chosen.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    // ---------------------------------------------------------------- request settings

    private GenerationParametersViewModel? _localParameters;
    private GenerationParametersViewModel? _apiParameters;

    /// <summary>What the local model is asked with. Answer length, history and wait follow the tuner.</summary>
    public GenerationParametersViewModel LocalParameters => _localParameters ??= new GenerationParametersViewModel(
        localization, new GenerationDefaults(0.4, 0.9, 3000, 8, 16, 10), ScheduleSettingsSave)
    {
        IsBudgetAutomatic = LocalModelAutoTune
    };

    /// <summary>What a hosted API is asked with.</summary>
    public GenerationParametersViewModel ApiParameters => _apiParameters ??= new GenerationParametersViewModel(
        // Enough room for a whole day's plan, and enough steps to work one out: the old pair of
        // 2000 tokens and 8 steps cut long answers off and gave up on anything that needed
        // reading, acting and checking in turn.
        localization, new GenerationDefaults(0.3, 1, 4000, 12, 24, 5), ScheduleSettingsSave);

    /// <summary>The set the advanced card shows: the one belonging to whoever is answering.</summary>
    public GenerationParametersViewModel ActiveParameters => IsLocalProviderActive ? LocalParameters : ApiParameters;

    public string ActiveParametersHint => localization.Text(IsLocalProviderActive ? "ParamsForLocal" : "ParamsForApi");

    private void LoadGenerationParameters(AppSettings settings)
    {
        LocalParameters.Load(settings.LocalTemperature, settings.LocalTopP, settings.LocalMaxTokens,
            settings.LocalMaxToolIterations, settings.LocalHistoryTurns, settings.LocalTimeoutMinutes);
        ApiParameters.Load(settings.AssistantTemperature, settings.AssistantTopP, settings.AssistantMaxTokens,
            settings.AssistantMaxToolIterations, settings.AssistantHistoryTurns, settings.AssistantTimeoutMinutes);
    }

    private void CopyGenerationParameters(AppSettings settings)
    {
        settings.LocalTemperature = Math.Clamp(LocalParameters.Temperature, 0, 2);
        settings.LocalTopP = Math.Clamp(LocalParameters.TopP, 0.05, 1);
        settings.LocalMaxTokens = LocalParameters.MaxTokens;
        settings.LocalMaxToolIterations = Math.Clamp((int)LocalParameters.MaxToolIterations, 1, 16);
        settings.LocalHistoryTurns = Math.Clamp((int)LocalParameters.HistoryTurns, 2, 64);
        settings.LocalTimeoutMinutes = Math.Clamp((int)LocalParameters.TimeoutMinutes, 1, 30);

        settings.AssistantTemperature = Math.Clamp(ApiParameters.Temperature, 0, 2);
        settings.AssistantTopP = Math.Clamp(ApiParameters.TopP, 0.05, 1);
        settings.AssistantMaxTokens = ApiParameters.MaxTokens;
        settings.AssistantMaxToolIterations = Math.Clamp((int)ApiParameters.MaxToolIterations, 1, 16);
        settings.AssistantHistoryTurns = Math.Clamp((int)ApiParameters.HistoryTurns, 2, 64);
        settings.AssistantTimeoutMinutes = Math.Clamp((int)ApiParameters.TimeoutMinutes, 1, 30);
    }

    // ---------------------------------------------------------------- the machine

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalModelManual))]
    private bool _localModelAutoTune = true;

    public bool IsLocalModelManual => !LocalModelAutoTune;

    [ObservableProperty] private bool _isDetectingHardware;
    [ObservableProperty] private string _hardwareCpuText = string.Empty;
    [ObservableProperty] private string _hardwareMemoryText = string.Empty;
    [ObservableProperty] private string _hardwareGpuText = string.Empty;
    [ObservableProperty] private string _localTuningTitle = string.Empty;
    [ObservableProperty] private string _localTuningSummary = string.Empty;
    [ObservableProperty] private string _localTuningWarning = string.Empty;
    [ObservableProperty] private string _localTuningNote = string.Empty;
    [ObservableProperty] private string _retiredModelFilesText = string.Empty;
    [ObservableProperty] private bool _hasRetiredModelFiles;

    /// <summary>What the last look at the machine found; null until the first look is back.</summary>
    private HardwareProfile? _hardware;
    private LocalTuning? _localTuning;
    private long _retiredModelBytes;

    /// <summary>Header readings per model file. A file on disk does not change what it needs.</summary>
    private readonly ConcurrentDictionary<string, ModelMemoryShape?> _modelShapes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A projector is read beside the weights; this is roughly what the catalogue's take.</summary>
    private const long ProjectorAllowance = 900L * 1024 * 1024;

    partial void OnLocalModelAutoTuneChanged(bool value)
    {
        LocalParameters.IsBudgetAutomatic = value;
        if (_loadingSettings) return;
        ScheduleSettingsSave();
        RetuneLocalModel();
    }

    [RelayCommand]
    private Task DetectHardwareAsync() => ProbeHardwareAsync();

    /// <summary>Started once the shell is up; the answer arrives a moment later and tunes the model.</summary>
    private void StartHardwareProbe() => _ = ProbeHardwareAsync();

    private async Task ProbeHardwareAsync()
    {
        if (IsDetectingHardware) return;
        IsDetectingHardware = true;
        RenderHardware();
        try
        {
            // Asking the engine about the card takes the better part of a second.
            var (hardware, retired) = await Task.Run(() => (localModels.ReadHardware(), localModels.RetiredDownloadBytes()));
            _hardware = hardware;
            ShowRetiredDownloads(retired);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Reading the hardware");
        }
        finally
        {
            IsDetectingHardware = false;
        }
        RenderHardware();
        RetuneLocalModel();
    }

    /// <summary>
    /// Works the numbers out again for the chosen model on this machine and, while the choice is
    /// automatic, puts them in place, where the next save and the next start of the engine find them.
    /// </summary>
    private void RetuneLocalModel()
    {
        if (_hardware is null || _loadingSettings)
        {
            RenderLocalTuning();
            return;
        }
        _localTuning = LocalModelTuner.Recommend(WithoutOwnUsage(_hardware), ModelShape(),
            gpuAllowed: true, extraBytes: IsVisionActive ? ProjectorAllowance : 0);
        if (LocalModelAutoTune) ApplyTuning(_localTuning);
        RenderLocalTuning();
    }

    private void ApplyTuning(LocalTuning tuning)
    {
        LocalModelGpuEnabled = tuning.UseGpu;
        LocalModelContextText = tuning.ContextTokens.ToString(CultureInfo.InvariantCulture);
        LocalModelThreadsText = tuning.Threads.ToString(CultureInfo.InvariantCulture);
        SelectedLocalModelIdleOption = LocalModelIdleOptions.FirstOrDefault(option =>
            option.Value == tuning.IdleMinutes.ToString(CultureInfo.InvariantCulture)) ?? SelectedLocalModelIdleOption;
        LocalParameters.ApplyBudget(tuning.MaxTokens, tuning.HistoryTurns, tuning.TimeoutMinutes);
    }

    /// <summary>
    /// The machine as it would be with the model not yet loaded. What a running engine holds
    /// counts as free, or the tuner would size the model against itself and find it tight.
    /// </summary>
    private HardwareProfile WithoutOwnUsage(HardwareProfile hardware)
    {
        if (localModels.GetResourceMetrics() is not { IsRunning: true } running) return hardware;
        var best = hardware.BestGpu;
        var gpus = hardware.Gpus.Select(gpu => ReferenceEquals(gpu, best) && gpu.FreeBytes > 0 && running.VideoMemoryBytes > 0
            ? gpu with { FreeBytes = Math.Min(gpu.DedicatedBytes, gpu.FreeBytes + running.VideoMemoryBytes) }
            : gpu).ToList();
        return hardware with
        {
            AvailableMemoryBytes = hardware.AvailableMemoryBytes > 0
                ? hardware.AvailableMemoryBytes + running.MemoryBytes
                : 0,
            Gpus = gpus
        };
    }

    /// <summary>The model's own figures when its file is on disk; the catalogue's until then.</summary>
    private ModelMemoryShape ModelShape()
    {
        var descriptor = LocalModelCatalog.Find(LocalModelId);
        var path = localModels.ModelFilePath(Selection);
        if (path is null) return descriptor.Shape;
        return _modelShapes.GetOrAdd(path, GgufMetadata.ReadShape)
               ?? ModelMemoryShape.Conventional(new FileInfo(path).Length);
    }

    private void RenderHardware()
    {
        var hardware = _hardware;
        if (hardware is null)
        {
            var waiting = IsDetectingHardware ? localization.Text("LocalHardwareDetecting") : string.Empty;
            HardwareCpuText = HardwareMemoryText = HardwareGpuText = waiting;
            return;
        }

        HardwareCpuText = hardware.EfficiencyCores > 0
            ? localization.Format("LocalHardwareCpuHybrid", hardware.PerformanceCores, hardware.EfficiencyCores, hardware.LogicalProcessors)
            : hardware.PhysicalCores > 0
                ? localization.Format("LocalHardwareCpuValue", hardware.PhysicalCores, hardware.LogicalProcessors)
                : localization.Format("LocalHardwareCpuThreads", hardware.LogicalProcessors);
        HardwareMemoryText = hardware.IsKnown
            ? localization.Format("LocalHardwareMemoryValue", FormatBytes(hardware.TotalMemoryBytes), FormatBytes(hardware.AvailableMemoryBytes))
            : localization.Text("LocalHardwareUnknown");
        HardwareGpuText = hardware.BestGpu is { } gpu
            ? gpu.FreeBytes > 0
                ? localization.Format("LocalHardwareGpuFree", gpu.Name, FormatBytes(gpu.DedicatedBytes), FormatBytes(gpu.FreeBytes))
                : localization.Format("LocalHardwareGpuValue", gpu.Name, FormatBytes(gpu.DedicatedBytes))
            : hardware.Gpus.FirstOrDefault(adapter => !adapter.IsIntegrated) is { } detected
                ? localization.Format("LocalHardwareGpuDetected", detected.Name)
            : hardware.Gpus.FirstOrDefault(adapter => adapter.IsIntegrated) is { } builtIn
                ? localization.Format("LocalHardwareGpuIntegrated", builtIn.Name)
                : localization.Text("LocalHardwareGpuNone");
    }

    private void RenderLocalTuning()
    {
        var tuning = _localTuning;
        if (tuning is null)
        {
            LocalTuningTitle = localization.Text(_hardware is null ? "LocalHardwareDetecting" : "LocalHardwareUnknown");
            LocalTuningSummary = LocalTuningWarning = LocalTuningNote = string.Empty;
            return;
        }

        var culture = localization.CurrentCulture;
        LocalTuningTitle = tuning.Placement switch
        {
            LocalPlacement.Gpu => localization.Text("LocalTuneFullGpu"),
            LocalPlacement.Split => localization.Format("LocalTuneSplit", (int)Math.Round(tuning.GpuShare * 100)),
            _ => localization.Text(_hardware?.BestGpu is null ? "LocalTuneCpuNoCard" : "LocalTuneCpu")
        };
        LocalTuningSummary = localization.Format("LocalTuneSummary",
            tuning.ContextTokens.ToString("N0", culture), tuning.Threads,
            tuning.MaxTokens.ToString("N0", culture), tuning.HistoryTurns);
        LocalTuningWarning = tuning.IsMemoryTight
            ? localization.Format("LocalTuneWarnMemory", FormatBytes(tuning.RequiredBytes), FormatBytes(Math.Max(0, tuning.AvailableBytes)))
            : string.Empty;
        LocalTuningNote = LocalModelAutoTune ? string.Empty : localization.Text("LocalTuneManual");
    }

    private void ShowRetiredDownloads(long bytes)
    {
        _retiredModelBytes = bytes;
        HasRetiredModelFiles = bytes > 0;
        RetiredModelFilesText = bytes > 0
            ? localization.Format("LocalModelLegacyFiles", string.Join(", ", LocalModelCatalog.RetiredQuantTags), FormatBytes(bytes))
            : string.Empty;
    }

    [RelayCommand]
    private async Task DeleteRetiredModelFilesAsync()
    {
        try
        {
            var freed = await localModels.DeleteRetiredDownloadsAsync();
            ShowRetiredDownloads(await Task.Run(localModels.RetiredDownloadBytes));
            Status = localization.Format("LocalModelLegacyDeleted", FormatBytes(freed));
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Deleting retired model files");
            Status = exception.Message;
        }
    }

    /// <summary>Every worded line of this part of the page again, after the language changed.</summary>
    private void RefreshLocalTuningText()
    {
        RenderHardware();
        RenderLocalTuning();
        ShowRetiredDownloads(_retiredModelBytes);
        LocalParameters.RefreshText();
        ApiParameters.RefreshText();
        OnPropertyChanged(nameof(ActiveParametersHint));
    }
}
