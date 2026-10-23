using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IntelligentScheduler.Desktop.Localization;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>Where a set of request settings starts, and where Reset puts it back.</summary>
public sealed record GenerationDefaults(
    double Temperature, double TopP, int MaxTokens, int ToolIterations, int HistoryTurns, int TimeoutMinutes);

/// <summary>
/// One set of request settings: sampling, the answer budget, and how much of the conversation
/// is replayed. The local model and a hosted API each have their own, because the numbers that
/// suit one are wrong for the other, and the settings page shows the set belonging to whichever
/// engine is answering.
/// </summary>
public sealed partial class GenerationParametersViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly Action _changed;
    private bool _loading;

    public GenerationParametersViewModel(LocalizationService localization, GenerationDefaults defaults, Action changed)
    {
        _localization = localization;
        _changed = changed;
        Defaults = defaults;
        Load(defaults.Temperature, defaults.TopP, defaults.MaxTokens, defaults.ToolIterations,
            defaults.HistoryTurns, defaults.TimeoutMinutes);
    }

    public GenerationDefaults Defaults { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureText))]
    private double _temperature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopPText))]
    private double _topP = 1;

    [ObservableProperty] private string _maxTokensText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxToolIterationsText))]
    private double _maxToolIterations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryTurnsText))]
    private double _historyTurns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeoutMinutesText))]
    private double _timeoutMinutes;

    /// <summary>
    /// True while the local tuner chooses the answer length, the history and the wait for this
    /// machine. The three controls are then shown, so the choice can be read, but not offered.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBudgetEditable))]
    private bool _isBudgetAutomatic;

    public bool IsBudgetEditable => !IsBudgetAutomatic;

    public string TemperatureText => Temperature.ToString("0.0", _localization.CurrentCulture);
    public string TopPText => TopP >= 1 ? _localization.Text("ParamOff") : TopP.ToString("0.00", _localization.CurrentCulture);
    public string MaxToolIterationsText => ((int)MaxToolIterations).ToString(_localization.CurrentCulture);
    public string HistoryTurnsText => ((int)HistoryTurns).ToString(_localization.CurrentCulture);
    public string TimeoutMinutesText => _localization.Format("ParamMinutes", (int)TimeoutMinutes);

    public int MaxTokens => int.TryParse(MaxTokensText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? Math.Clamp(value, 64, 32_000)
        : Defaults.MaxTokens;

    partial void OnTemperatureChanged(double value) => Changed();
    partial void OnTopPChanged(double value) => Changed();
    partial void OnMaxTokensTextChanged(string value) => Changed();
    partial void OnMaxToolIterationsChanged(double value) => Changed();
    partial void OnHistoryTurnsChanged(double value) => Changed();
    partial void OnTimeoutMinutesChanged(double value) => Changed();

    private void Changed()
    {
        if (!_loading) _changed();
    }

    /// <summary>Takes stored values without each of them scheduling a save of its own.</summary>
    public void Load(double temperature, double topP, int maxTokens, int toolIterations, int historyTurns, int timeoutMinutes)
    {
        _loading = true;
        try
        {
            Temperature = Math.Clamp(temperature, 0, 2);
            TopP = Math.Clamp(topP <= 0 ? 1 : topP, 0.05, 1);
            MaxTokensText = Math.Clamp(maxTokens <= 0 ? Defaults.MaxTokens : maxTokens, 64, 32_000)
                .ToString(CultureInfo.InvariantCulture);
            MaxToolIterations = Math.Clamp(toolIterations <= 0 ? Defaults.ToolIterations : toolIterations, 1, 16);
            HistoryTurns = Math.Clamp(historyTurns <= 0 ? Defaults.HistoryTurns : historyTurns, 2, 64);
            TimeoutMinutes = Math.Clamp(timeoutMinutes <= 0 ? Defaults.TimeoutMinutes : timeoutMinutes, 1, 30);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The tuner's three numbers. Saved like any other change, which is the point of them.</summary>
    public void ApplyBudget(int maxTokens, int historyTurns, int timeoutMinutes)
    {
        MaxTokensText = maxTokens.ToString(CultureInfo.InvariantCulture);
        HistoryTurns = historyTurns;
        TimeoutMinutes = timeoutMinutes;
    }

    /// <summary>Back to where this set starts. While the tuner owns the budget, the budget is its to put back.</summary>
    public void Reset()
    {
        Temperature = Defaults.Temperature;
        TopP = Defaults.TopP;
        MaxToolIterations = Defaults.ToolIterations;
        if (IsBudgetAutomatic) return;
        MaxTokensText = Defaults.MaxTokens.ToString(CultureInfo.InvariantCulture);
        HistoryTurns = Defaults.HistoryTurns;
        TimeoutMinutes = Defaults.TimeoutMinutes;
    }

    /// <summary>Every worded figure again, after the language changed.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(TopPText));
        OnPropertyChanged(nameof(MaxToolIterationsText));
        OnPropertyChanged(nameof(HistoryTurnsText));
        OnPropertyChanged(nameof(TimeoutMinutesText));
    }
}
