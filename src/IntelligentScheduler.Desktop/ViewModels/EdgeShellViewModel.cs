using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Desktop.ViewModels;

public sealed partial class EdgeShellViewModel(
    ITaskRepository taskRepository,
    ICalendarRepository calendarRepository,
    IBlockRepository blockRepository,
    ISettingsRepository settingsRepository,
    IAutostartService autostartService,
    IAssistantService assistant,
    ILocalModelRuntime localModels,
    ISecretProtector secrets,
    ShellThemeService themeService,
    FontCatalogService fontCatalog,
    LocalizationService localization,
    CrashReporter crashReporter,
    IChatRepository chatRepository,
    IUserMemoryRepository memoryRepository,
    IScheduleActionExecutor actionExecutor,
    ITaskExecutionRepository executionRepository,
    IEventOutcomeRepository outcomeRepository,
    IEventFollowUpRepository followUpRepository,
    IUpdateService updateService,
    ICloudSyncService cloudSync,
    IProjectRepository projectRepository) : ObservableObject
{
    private AppSettings? _settings;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdgeShell))]
    private bool _isWindowedShell;
    public bool IsEdgeShell => !IsWindowedShell;
    private TaskItem? _editedTask;
    private CalendarEvent? _editedEvent;
    private CalendarEvent? _eventToCopy;
    private bool _isClearDayPending;
    private TimeSpan _pendingDayShift;
    private TimeBlock? _editedTaskBlock;
    private DateTimeOffset _taskBlockOriginalStartUtc;
    private DateTimeOffset _taskBlockOriginalEndUtc;
    private TimeSelectionPurpose _timeSelectionPurpose = TimeSelectionPurpose.EventStart;
    private TaskBlockPendingAction _pendingTaskBlockAction;
    private CancellationTokenSource? _assistantCancellation;
    private CancellationTokenSource? _localModelDownloadCancellation;
    private AssistantAttachment? _pendingAssistantAttachment;
    private CancellationTokenSource? _settingsSaveDelay;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _autostartGate = new(1, 1);
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _resourceMonitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loadingSettings = true;
    private bool _synchronizingAutostartState;
    private bool _synchronizingColorValues;
    private bool _synchronizingMascotColor;
    private bool _changingThemeColors;
    private bool _themeColorPreviewPending;
    private readonly Dictionary<string, MarelianThemeProfile> _themeColorOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeThemeColorKey;
    private int _dateSelectionVersion;
    private int _autostartChangeVersion;
    private DateOnly _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private CalendarSelectionPurpose _calendarSelectionPurpose = CalendarSelectionPurpose.Schedule;
    /// <summary>Clock format and language the hour rail was last built for; see <c>RebuildSchedulerHourLabels</c>.</summary>
    private string? _schedulerHourFormat;
    private string? _calendarWeekDayCulture;
    private bool _localModelEventsAttached;
    private long _lastScheduleRefreshMinute = -1;
    /// <summary>Mirrors the transition declared on the assistant sheet, so the two end together.</summary>
    private static readonly TimeSpan ChatSheetLeaveDuration = TimeSpan.FromMilliseconds(190);
    private CancellationTokenSource? _chatSheetLeaving;

    [ObservableProperty] private bool _isHome = true;
    [ObservableProperty] private bool _isTasks;
    [ObservableProperty] private bool _isCalendar;
    [ObservableProperty] private bool _isAssistant;
    [ObservableProperty] private bool _isSettings;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneral))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAppearance))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAssistant))]
    [NotifyPropertyChangedFor(nameof(IsSettingsData))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAbout))]
    private string _settingsSection = "general";
    [ObservableProperty] private bool _isAssistantAdvancedOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSheetOpen))]
    [NotifyPropertyChangedFor(nameof(IsChatDocked))]
    private bool _isChatOpen;
    /// <summary>
    /// Which of the assistant's two shapes is in force. Remembered across openings, because an
    /// expand button that forgets what it was told is a button that has to be pressed twice for
    /// every conversation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSheetOpen))]
    [NotifyPropertyChangedFor(nameof(IsChatDocked))]
    [NotifyPropertyChangedFor(nameof(ChatExpandTooltip))]
    private bool _isChatExpanded;
    /// <summary>
    /// Whether the sheet is still on the screen at all. It arrives with <see cref="IsChatSheetOpen"/>
    /// and leaves a moment behind it, because a control that has already been taken out of the
    /// tree has no frames left to fold back toward the petal on — and closing the assistant, or
    /// widening it, would be a cut where every other move in the shell is a movement.
    /// </summary>
    [ObservableProperty] private bool _isChatSheetPresent;
    [ObservableProperty] private bool _isCalendarExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLocalModelsExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelMenuLevel1))]
    [NotifyPropertyChangedFor(nameof(ShowModelMenuLevel2))]
    private bool _isModelMenuOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelMenuLevel1))]
    [NotifyPropertyChangedFor(nameof(ShowModelMenuLevel2))]
    private bool _isLocalModelsSubMenu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelMenuLevel1))]
    [NotifyPropertyChangedFor(nameof(ShowModelMenuLevel2))]
    private bool _isSwitchModelConfirmationOpen;

    [ObservableProperty] private string _switchModelConfirmationTitle = string.Empty;
    [ObservableProperty] private string _switchModelConfirmationMessage = string.Empty;
    [ObservableProperty] private string? _switchModelTargetId;
    [ObservableProperty] private string? _switchModelTargetProvider;
    [ObservableProperty] private bool _switchModelIsTargetInstalled;
    [ObservableProperty] private bool _switchModelIsTargetLocal;

    public bool ShowModelMenuLevel1 => IsModelMenuOpen && !IsLocalModelsSubMenu && !IsSwitchModelConfirmationOpen;
    public bool ShowModelMenuLevel2 => IsModelMenuOpen && IsLocalModelsSubMenu && !IsSwitchModelConfirmationOpen;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private DateTime? _calendarDate = DateTime.Today;
    [ObservableProperty] private string _dateTitle = string.Empty;
    [ObservableProperty] private string _daySummary = string.Empty;
    [ObservableProperty] private string _nextTitle = string.Empty;
    /// <summary>
    /// What the day card and the widget's event block say above the title. A reminder borrows
    /// that line in the widget while it lasts, which is why the two are separate properties.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeekEventEyebrow))]
    private string _nextLabel = string.Empty;
    [ObservableProperty] private string _nextMeta = string.Empty;
    [ObservableProperty] private double _progress;

    [ObservableProperty] private string _progressText = "0%";

    /// <summary>
    /// True once any of the day has been answered for. It used to be "the bar is above half a
    /// percent", which hid the bar on exactly the days worth showing it: a day where everything
    /// was skipped records nothing and so scored zero, and read as a day nobody had touched.
    /// </summary>
    [ObservableProperty] private bool _hasDayRecord;

    [ObservableProperty] private bool _isEventComposerVisible;
    [ObservableProperty] private string _eventTitle = string.Empty;
    [ObservableProperty] private DateTimeOffset? _eventDate = DateTimeOffset.Now;
    [ObservableProperty] private TimeSpan? _eventTime = TimeSpan.FromHours(12);
    [ObservableProperty] private TimeSpan? _eventEndTime = TimeSpan.FromHours(13);
    [ObservableProperty] private TimeSpan? _timePickerValue = TimeSpan.FromHours(12);
    [ObservableProperty] private bool _isTimePickerOpen;
    [ObservableProperty] private bool _isEditingStartTime = true;
    [ObservableProperty] private decimal _eventDuration = 60;
    [ObservableProperty] private string _eventActionText = string.Empty;
    [ObservableProperty] private string _eventValidationMessage = string.Empty;
    [ObservableProperty] private bool _isEventSaveConfirmationVisible;
    [ObservableProperty] private ChoiceOption? _selectedEventCategoryOption;
    [ObservableProperty] private ChoiceOption? _selectedEventPriorityOption;
    [ObservableProperty] private ChoiceOption? _selectedEventRecurrenceOption;
    [ObservableProperty] private ChoiceOption? _selectedEventEditScopeOption;
    // ComboBox rebuilds (notably while a localized list is replaced) briefly clear SelectedItem.
    // Keep the last real choices as values so saving a recurring event cannot turn that transient
    // null into General/Normal metadata for every generated occurrence.
    private EventCategory _eventCategoryDraft = EventCategory.General;
    private EventPriority _eventPriorityDraft = EventPriority.Normal;
    private EventRecurrence _eventRecurrenceDraft = EventRecurrence.None;
    [ObservableProperty] private bool _isDayActionsOpen;
    [ObservableProperty] private bool _isCopyDayConfirmationVisible;
    [ObservableProperty] private string _copyDayTargetText = string.Empty;
    [ObservableProperty] private bool _isEventCopyConfirmationVisible;
    [ObservableProperty] private string _eventCopyConfirmationText = string.Empty;
    [ObservableProperty] private decimal _dayShiftHours = 1;
    [ObservableProperty] private decimal _dayShiftMinutes;
    [ObservableProperty] private bool _isDayShiftEarlier;
    [ObservableProperty] private bool _isShiftDayConfirmationVisible;
    [ObservableProperty] private string _shiftDayConfirmationText = string.Empty;
    /// <summary>Whether the shift section is showing its second row of answers.</summary>
    [ObservableProperty] private bool _isDayShiftMoreOpen;
    /// <summary>Whole days the shift carries the day, on top of the hours and minutes.</summary>
    [ObservableProperty] private decimal _dayShiftDays;
    /// <summary>Whether the shift is given as a day to land on rather than as an amount.</summary>
    [ObservableProperty] private bool _isDayShiftByDate;
    [ObservableProperty] private string _dayShiftTargetDateLabel = string.Empty;
    private DateOnly _dayShiftTargetDate = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
    /// <summary>
    /// The plan a shift by whole days makes. An amount of clock time is still applied as one,
    /// straight over the range; days are moved event by event so each keeps its own time of day.
    /// </summary>
    private IReadOnlyList<EventMove> _pendingDayShiftMoves = [];
    private int _copyDayOffsetDays = 1;
    private int _eventEndDayOffset;
    [ObservableProperty] private bool _isScheduleExpanded = true;
    [ObservableProperty] private bool _isTasksExpanded = true;

    [ObservableProperty] private bool _isTaskEditorVisible;
    [ObservableProperty] private string _editTitle = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private decimal _editDuration = 30;
    [ObservableProperty] private decimal _editPriority = 50;
    [ObservableProperty] private DateTimeOffset? _editDeadline;
    [ObservableProperty] private DateTimeOffset? _editEarliestStart;
    [ObservableProperty] private bool _editSplittable;
    [ObservableProperty] private string _editMinBlockText = "30";
    [ObservableProperty] private bool _isTaskAdvancedVisible;
    [ObservableProperty] private ChoiceOption? _selectedTaskPriorityOption;
    [ObservableProperty] private ChoiceOption? _selectedTaskPreferredTimeOption;
    [ObservableProperty] private ChoiceOption? _selectedTaskMaxSplitsOption;
    [ObservableProperty] private string _taskActionText = string.Empty;

    [ObservableProperty] private bool _isTaskBlockEditorVisible;
    [ObservableProperty] private string _taskBlockTitle = string.Empty;
    [ObservableProperty] private DateTimeOffset? _taskBlockDate;
    [ObservableProperty] private TimeSpan? _taskBlockStartTime;
    [ObservableProperty] private TimeSpan? _taskBlockEndTime;
    [ObservableProperty] private bool _taskBlockIsLocked;
    [ObservableProperty] private string _taskBlockCompletedMinutesText = "30";
    [ObservableProperty] private string _taskBlockValidationMessage = string.Empty;
    [ObservableProperty] private string _taskBlockConfirmationText = string.Empty;
    [ObservableProperty] private bool _isTaskBlockConfirmationVisible;

    [ObservableProperty] private string _assistantInput = string.Empty;
    [ObservableProperty] private string _assistantState = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalModelResourceMonitor))]
    [NotifyPropertyChangedFor(nameof(IsLocalProviderActive))]
    [NotifyPropertyChangedFor(nameof(IsExternalProviderActive))]
    [NotifyPropertyChangedFor(nameof(IsMcpProviderActive))]
    [NotifyPropertyChangedFor(nameof(ActiveModelDisplayName))]
    [NotifyPropertyChangedFor(nameof(LocalProviderMenuSubtitle))]
    [NotifyPropertyChangedFor(nameof(CanToggleLocalModelExecution))]
    private string _assistantProvider = nameof(Domain.AssistantProvider.Local);

    /// <summary>Kept so a failed send can put the file back rather than losing it.</summary>
    private (string Name, string Path)? _lastAttachment;

    [ObservableProperty] private string _attachedFileMeta = string.Empty;
    [ObservableProperty] private bool _isAttachmentImage;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _attachmentPreview;

    partial void OnAttachmentPreviewChanging(Avalonia.Media.Imaging.Bitmap? value)
    {
        if (!ReferenceEquals(AttachmentPreview, value)) AttachmentPreview?.Dispose();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentWarning))]
    private string _attachmentWarning = string.Empty;

    public bool HasAttachmentWarning => AttachmentWarning.Length > 0;

    [ObservableProperty] private string _assistantEndpoint = "http://localhost:11434";
    [ObservableProperty] private string _assistantModel = "qwen3:4b";
    [ObservableProperty] private string _assistantAccessLevel = nameof(Domain.AssistantAccessLevel.ConfirmAll);
    [ObservableProperty] private bool _shareContextWithExternalAi;
    [ObservableProperty] private string _assistantApiKey = string.Empty;
    [ObservableProperty] private bool _hasSavedApiKey;
    [ObservableProperty] private string _mcpTransport = "StreamableHttp";
    /// <summary>
    /// The MCP server's own address. It used to share the chat API's field, which meant every
    /// API URL also announced an MCP server at the same place — and one dead protocol handshake
    /// before each message.
    /// </summary>
    [ObservableProperty] private string _mcpEndpoint = string.Empty;
    [ObservableProperty] private string _mcpCommand = string.Empty;
    [ObservableProperty] private string _mcpArguments = string.Empty;
    [ObservableProperty] private string _mcpToolName = "assistant";
    [ObservableProperty] private bool _mcpToolsEnabled = true;
    [ObservableProperty] private bool _assistantMemoryEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLocalModelDisplayName))]
    [NotifyPropertyChangedFor(nameof(RunningLocalModelDisplayName))]
    [NotifyPropertyChangedFor(nameof(LocalProviderMenuSubtitle))]
    [NotifyPropertyChangedFor(nameof(ActiveModelDisplayName))]
    [NotifyPropertyChangedFor(nameof(SelectedQuantLabel))]
    [NotifyPropertyChangedFor(nameof(IsCustomModelSelected))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogModelActions))]
    [NotifyPropertyChangedFor(nameof(SelectedModelSupportsVision))]
    [NotifyPropertyChangedFor(nameof(IsVisionActive))]
    [NotifyPropertyChangedFor(nameof(NeedsProjectorDownload))]
    [NotifyPropertyChangedFor(nameof(NeedsLocalModelDownload))]
    [NotifyPropertyChangedFor(nameof(CanDownloadLocalModel))]
    [NotifyPropertyChangedFor(nameof(ShowProjectorPicker))]
    [NotifyPropertyChangedFor(nameof(VisionRequirementText))]
    private string _localModelId = LocalModelCatalog.DefaultModelId;

    /// <summary>
    /// Image input. Turning this on is what makes the projector a missing piece — and the
    /// button that fetches it is only offered while something is actually missing, so without
    /// these notifications the switch was flipped, nothing appeared, and the projector could
    /// never be downloaded at all.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLocalModelDownload))]
    [NotifyPropertyChangedFor(nameof(CanDownloadLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanStartLocalModel))]
    [NotifyPropertyChangedFor(nameof(LocalModelDownloadActionText))]
    [NotifyPropertyChangedFor(nameof(LocalModelExecutionTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowProjectorPicker))]
    [NotifyPropertyChangedFor(nameof(VisionRequirementText))]
    private bool _localModelVisionEnabled;

    [ObservableProperty] private string _localModelContextText = "8192";
    [ObservableProperty] private string _localModelThreadsText = "0";

    /// <summary>
    /// Whether the model may use the graphics card. Turning it over restarts the engine, so
    /// the switch says so rather than pretending the change is free.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalModelAcceleratorHint))]
    private bool _localModelGpuEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomModel))]
    [NotifyPropertyChangedFor(nameof(CustomModelFileName))]
    [NotifyPropertyChangedFor(nameof(ActiveModelDisplayName))]
    [NotifyPropertyChangedFor(nameof(VisionRequirementText))]
    private string _customModelPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomProjector))]
    [NotifyPropertyChangedFor(nameof(CustomProjectorFileName))]
    [NotifyPropertyChangedFor(nameof(VisionRequirementText))]
    private string _customProjectorPath = string.Empty;

    // Request shaping lives in two sets now, one per engine: see EdgeShellViewModel.LocalTuning.
    [ObservableProperty] private string _assistantExtraInstructions = string.Empty;
    [ObservableProperty] private ChoiceOption? _selectedLocalModelIdleOption;
    [ObservableProperty] private string _localModelStateText = string.Empty;
    [ObservableProperty] private string _localModelTransferText = string.Empty;
    [ObservableProperty] private double _localModelProgress;
    [ObservableProperty] private bool _isLocalModelTransferring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalModelResourceMonitor))]
    [NotifyPropertyChangedFor(nameof(CanStartLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanStopLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanToggleLocalModelExecution))]
    [NotifyPropertyChangedFor(nameof(LocalModelExecutionTooltip))]
    private bool _isLocalModelRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalModelResourceMonitor))]
    [NotifyPropertyChangedFor(nameof(CanStartLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanStopLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanToggleLocalModelExecution))]
    [NotifyPropertyChangedFor(nameof(LocalModelExecutionTooltip))]
    private bool _isLocalModelStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLocalModelDownload))]
    [NotifyPropertyChangedFor(nameof(CanStartLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanToggleLocalModelExecution))]
    [NotifyPropertyChangedFor(nameof(LocalModelExecutionTooltip))]
    [NotifyPropertyChangedFor(nameof(LocalModelDownloadActionText))]
    private bool _isSelectedLocalModelInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLocalModelDownload))]
    [NotifyPropertyChangedFor(nameof(CanDownloadLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanStartLocalModel))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLocalModel))]
    [NotifyPropertyChangedFor(nameof(VisionRequirementText))]
    private bool _isSelectedProjectorInstalled;
    [ObservableProperty] private string _shellTheme = nameof(Domain.ShellTheme.Marelian);
    [ObservableProperty] private string _shellThemeMode = nameof(Domain.ShellThemeMode.Dark);
    [ObservableProperty] private string _themeColorSource = nameof(Domain.ThemeColorSource.Preset);
    [ObservableProperty] private string _visualPalette = nameof(Domain.VisualPalette.Vibrant);
    [ObservableProperty] private string _appFontFamily = FontCatalogService.InterId;
    [ObservableProperty] private string _customAccent = "#D75053";
    [ObservableProperty] private string _customBackground = "#13110F";
    [ObservableProperty] private Color _customAccentColor = Color.Parse("#D75053");
    [ObservableProperty] private Color _customBackgroundColor = Color.Parse("#13110F");
    [ObservableProperty] private bool _isAccentColorPickerOpen;
    [ObservableProperty] private bool _isBackgroundColorPickerOpen;
    /// <summary>Empty while the creature follows the theme, a hex colour once one is chosen for it.</summary>
    [ObservableProperty] private string _mascotColor = string.Empty;
    [ObservableProperty] private Color _mascotColorValue = Color.Parse("#D75053");
    [ObservableProperty] private bool _useThemeMascotColor = true;
    [ObservableProperty] private bool _isMascotColorPickerOpen;
    /// <summary>Whether the creature is drawn in the quick widget at all.</summary>
    [ObservableProperty] private bool _showMascot = true;
    [ObservableProperty] private bool _enableTransparency = true;
    [ObservableProperty] private double _transparencyIntensity = 10;
    [ObservableProperty] private string _revealMode = nameof(EdgeRevealMode.Hover);
    /// <summary>Grace period before the quick widget starts fading, in milliseconds.</summary>
    [ObservableProperty] private double _widgetHideDelayMs = 1500;
    /// <summary>
    /// The widget stays until it is closed. The grace period answers "is this still wanted?"
    /// for a widget that was glanced at; somebody who keeps it open beside their work is
    /// answering that question every time they look at it, and does not want to be asked.
    /// </summary>
    [ObservableProperty] private bool _widgetKeepOpen;
    [ObservableProperty] private bool _pinOpen;
    [ObservableProperty] private LanguageOption? _selectedLanguage;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private bool _startHidden;
    [ObservableProperty] private bool _keepOnTop = true;
    [ObservableProperty] private bool _use24HourClock = true;
    [ObservableProperty] private string _currentTimeText = string.Empty;
    [ObservableProperty] private string _settingsSaveState = string.Empty;
    [ObservableProperty] private ChoiceOption? _selectedThemeOption;
    [ObservableProperty] private ChoiceOption? _selectedThemeModeOption;
    [ObservableProperty] private ChoiceOption? _selectedColorSourceOption;
    [ObservableProperty] private ChoiceOption? _selectedVisualPaletteOption;
    [ObservableProperty] private ChoiceOption? _selectedFontOption;
    [ObservableProperty] private ChoiceOption? _selectedRevealOption;
    [ObservableProperty] private ChoiceOption? _selectedAssistantProviderOption;
    [ObservableProperty] private ChoiceOption? _selectedAssistantAccessLevelOption;
    [ObservableProperty] private ChoiceOption? _selectedMcpTransportOption;
    [ObservableProperty] private bool _isCalendarPopupOpen;
    [ObservableProperty] private bool _isEventDatePickerOpen;
    [ObservableProperty] private bool _isTaskDatePickerOpen;
    [ObservableProperty] private string _calendarMonthTitle = string.Empty;
    [ObservableProperty] private string _selectedDateLabel = string.Empty;
    [ObservableProperty] private string _eventDateLabel = string.Empty;
    [ObservableProperty] private string _editDeadlineLabel = string.Empty;
    [ObservableProperty] private string _editEarliestStartLabel = string.Empty;
    [ObservableProperty] private string _taskBlockDateLabel = string.Empty;
    [ObservableProperty] private string _scheduleCountText = string.Empty;
    [ObservableProperty] private string _tasksCountText = string.Empty;
    [ObservableProperty] private string _eventTimeText = "12:00";
    [ObservableProperty] private string _eventEndTimeText = "13:00";
    [ObservableProperty] private string _editDurationText = "30";
    [ObservableProperty] private string _editPriorityText = "50";
    [ObservableProperty] private string _composerInput = string.Empty;
    [ObservableProperty] private string _composerPlaceholder = string.Empty;
    [ObservableProperty] private string _attachedFileName = string.Empty;
    [ObservableProperty] private string _attachedFilePath = string.Empty;
    [ObservableProperty] private string _schedulerWeekTitle = string.Empty;
    [ObservableProperty] private string _localModelMemoryUsageText = "0 MB";
    [ObservableProperty] private string _localModelCpuUsageText = "0%";
    [ObservableProperty] private string _localModelUptimeText = "00:00";
    [ObservableProperty] private string _localModelMonitorTag = string.Empty;

    /// <summary>"GPU" or "CPU" — where the running model actually is, not where it was asked to be.</summary>
    [ObservableProperty] private string _localModelAcceleratorText = string.Empty;

    /// <summary>Video memory the model is holding on the card, read live off the machine.</summary>
    [ObservableProperty] private string _localModelVideoMemoryText = string.Empty;

    /// <summary>What that is a share of: "of 8 GB", blank when the card's size is unknown.</summary>
    [ObservableProperty] private string _localModelVideoMemoryScaleText = string.Empty;

    /// <summary>The device the engine named, shown as the capsule's tooltip.</summary>
    [ObservableProperty] private string _localModelAcceleratorDetail = string.Empty;

    /// <summary>The card's share of work, or a dash where the machine keeps no such figure.</summary>
    [ObservableProperty] private string _localModelGpuUsageText = string.Empty;

    /// <summary>What the memory figure is a share of: "of 32 GB", blank when the total is unknown.</summary>
    [ObservableProperty] private string _localModelMemoryScaleText = string.Empty;

    // The bar under each of the four figures. A percentage is a share of something and reads as
    // one; a number of bytes is not, so its bar is its share of what holds it — the memory in
    // the machine, or the memory on the card.
    [ObservableProperty] private double _localModelCpuFraction;
    [ObservableProperty] private double _localModelGpuFraction;
    [ObservableProperty] private double _localModelMemoryFraction;
    [ObservableProperty] private double _localModelVideoMemoryFraction;

    [ObservableProperty] private bool _isLocalModelOnGpu;

    public string LocalModelAcceleratorHint => localization.Text(
        LocalModelGpuEnabled ? "LocalModelGpuOnHint" : "LocalModelGpuOffHint");
    public bool ShowLocalModelResourceMonitor => (IsLocalModelRunning || localModels.Status.State == LocalModelRuntimeState.Running) && IsLocalProviderActive;
    public bool HasInstalledLocalModels => LocalModelQuickOptions.Count > 0;
    public string RunningLocalModelDisplayName => !string.IsNullOrWhiteSpace(LocalModelMonitorTag)
        ? LocalModelMonitorTag
        : LocalModelCatalog.Find(localModels.Status.ModelId).DisplayName;
    public string LocalProviderMenuSubtitle => HasInstalledLocalModels
        ? SelectedLocalModelDisplayName
        : localization.Text("LocalModelNotInstalled");

    public ObservableCollection<DayChipViewModel> WeekDays { get; } = [];
    public ObservableCollection<CompactTaskViewModel> Tasks { get; } = [];
    public ObservableCollection<AgendaItemViewModel> Agenda { get; } = [];
    public ObservableCollection<AssistantMessageViewModel> Messages { get; } = [];
    public ObservableCollection<AssistantSuggestionViewModel> Suggestions { get; } = [];
    public ObservableCollection<AssistantActionViewModel> AssistantActions { get; } = [];
    public ObservableCollection<ChoiceOption> ThemeOptions { get; } = [];
    public ObservableCollection<ChoiceOption> ThemeModeOptions { get; } = [];
    public ObservableCollection<ChoiceOption> ColorSourceOptions { get; } = [];
    public ObservableCollection<ChoiceOption> VisualPaletteOptions { get; } = [];
    public ObservableCollection<ChoiceOption> FontOptions { get; } = [];
    public ObservableCollection<ChoiceOption> RevealOptions { get; } = [];
    public ObservableCollection<ChoiceOption> AssistantProviderOptions { get; } = [];
    public ObservableCollection<ChoiceOption> AssistantAccessLevelOptions { get; } = [];
    public ObservableCollection<ChoiceOption> McpTransportOptions { get; } = [];
    public ObservableCollection<ChoiceOption> LocalModelIdleOptions { get; } = [];
    public ObservableCollection<LocalModelOptionViewModel> LocalModelOptions { get; } = [];
    public ObservableCollection<ChoiceOption> EventCategoryOptions { get; } = [];
    public ObservableCollection<ChoiceOption> EventPriorityOptions { get; } = [];
    public ObservableCollection<ChoiceOption> EventRecurrenceOptions { get; } = [];
    public ObservableCollection<ChoiceOption> EventEditScopeOptions { get; } = [];
    public ObservableCollection<ChoiceOption> TaskPriorityOptions { get; } = [];
    public ObservableCollection<ChoiceOption> TaskPreferredTimeOptions { get; } = [];
    public ObservableCollection<ChoiceOption> TaskMaxSplitsOptions { get; } = [];
    public ObservableCollection<string> CalendarWeekDays { get; } = [];
    public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = [];
    public ObservableCollection<SchedulerHourViewModel> SchedulerHours { get; } = [];
    public ObservableCollection<SchedulerDayViewModel> SchedulerWeekDays { get; } = [];
    public ObservableCollection<SchedulerBlockViewModel> SchedulerBlocks { get; } = [];
    public IReadOnlyList<LanguageOption> LanguageOptions => localization.SupportedLanguages;
    /// <summary>The MCP address row, which only a Streamable HTTP server has any use for.</summary>
    public bool ShowMcpHttpSettings => ShowMcpSettings && McpTransport == "StreamableHttp";

    /// <summary>
    /// Whether temperature, top-p, the answer limit and the tool budget are worth showing.
    ///
    /// They shape a chat request, so they reach the API and the managed local model alike — a
    /// llama-server speaks the same protocol the hosted endpoints do. The MCP bridge does not:
    /// it is handed a prompt and answers on its own, and none of these numbers travel with it.
    /// Controls that cannot change anything are worse than absent.
    /// </summary>
    public bool ShowSamplingParameters => AssistantProvider != nameof(Domain.AssistantProvider.Mcp);

    /// <summary>The tool loop belongs to the model, so the bridge has no round trips to budget.</summary>
    public bool ShowToolLoopParameters => ShowSamplingParameters;

    /// <summary>Said once, in place of the controls, when the bridge is answering.</summary>
    public bool ShowMcpAdvancedNotice => !ShowSamplingParameters;
    public bool ShowApiSettings => AssistantProvider == nameof(Domain.AssistantProvider.OpenAiCompatible);
    public bool ShowApiKey => AssistantProvider is nameof(Domain.AssistantProvider.OpenAiCompatible) or nameof(Domain.AssistantProvider.Mcp);
    public bool ShowMcpSettings => AssistantProvider == nameof(Domain.AssistantProvider.Mcp);
    public bool ShowStdioSettings => ShowMcpSettings && McpTransport == "Stdio";
    public bool ShowModel => AssistantProvider == nameof(Domain.AssistantProvider.OpenAiCompatible);
    public bool ShowLocalModelSettings => AssistantProvider == nameof(Domain.AssistantProvider.Local);
    public bool ShowExternalContextConsent => AssistantProvider != nameof(Domain.AssistantProvider.Local);
    public string AssistantAccessLevelLabel => SelectedAssistantAccessLevelOption?.Label ?? localization.Text("AiAccessConfirmAll");
    public string AssistantAccessLevelDescription => localization.Text(AssistantAccessLevel switch
    {
        nameof(Domain.AssistantAccessLevel.AutoSafe) => "AiAccessAutoSafeHint",
        nameof(Domain.AssistantAccessLevel.FullAccess) => "AiAccessFullHint",
        _ => "AiAccessConfirmAllHint"
    });
    // What the assistant is allowed to do on its own is three named steps, so it is offered as
    // three: a list with one entry showing is a menu the reader has to open to find out where
    // they stand, and where they stand here is worth knowing at a glance.
    public bool IsAccessConfirmAll => AssistantAccessLevel == nameof(Domain.AssistantAccessLevel.ConfirmAll);
    public bool IsAccessAutoSafe => AssistantAccessLevel == nameof(Domain.AssistantAccessLevel.AutoSafe);
    public bool IsAccessFullAccess => AssistantAccessLevel == nameof(Domain.AssistantAccessLevel.FullAccess);
    public bool IsCustomColor => ThemeColorSource == nameof(Domain.ThemeColorSource.Custom);
    public IBrush CustomAccentBrush => new SolidColorBrush(CustomAccentColor);
    public IBrush CustomBackgroundBrush => new SolidColorBrush(CustomBackgroundColor);
    public IBrush MascotColorBrush => new SolidColorBrush(MascotColorValue);
    /// <summary>
    /// What the mascot is painted with, or null while it should keep following the theme. The
    /// widget binds the theme's own accent resource underneath this one, so the default stays a
    /// live colour rather than a copy taken at the moment the settings were read.
    /// </summary>
    public IBrush? MascotAccentBrush => UseThemeMascotColor ? null : new SolidColorBrush(MascotColorValue);
    /// <summary>
    /// How wide the widget's column of blocks is. The creature owns the right half of the clock
    /// block, and the twelve-hour reading is set smaller so as not to run under it. With nobody
    /// there that half is dead space, so the widget gives it back rather than showing it.
    /// </summary>
    /// <summary>
    /// How wide the widget's one column is. Deliberately not something a reminder gets a say in:
    /// the reminder expands the block it lands in, and a column that also changed width would
    /// move every other block sideways to make room for two lines of text that wrap perfectly
    /// well without it.
    /// </summary>
    public double PeekColumnWidth => ShowMascot ? 196 : 150;
    public bool IsDarkThemeMode => ShellThemeMode == nameof(Domain.ShellThemeMode.Dark);
    public bool IsLightThemeMode => !IsDarkThemeMode;
    public bool IsSettingsGeneral => SettingsSection == "general";
    public bool IsSettingsAppearance => SettingsSection == "appearance";
    /// <summary>
    /// One AI section. Connection and the local engine used to be separate tabs, which meant
    /// choosing "local model" in one place and then hunting for the download in another.
    /// </summary>
    public bool IsSettingsAssistant => SettingsSection == "ai";
    public bool IsSettingsData => SettingsSection == "data";
    public bool IsSettingsAbout => SettingsSection == "about";
    public bool IsDayShiftLater => !IsDayShiftEarlier;
    public string DayShiftAmountLabel => $"{(int)DayShiftHours:00}:{(int)DayShiftMinutes:00}";
    public string DayShiftDaysLabel => localization.Format("MetricDays", (int)DayShiftDays);
    public string TaskDurationLabel
    {
        get
        {
            var minutes = int.TryParse(EditDurationText, out var parsed) ? Math.Max(0, parsed) : 0;
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }
    }
    public bool IsTimePickerPeriodVisible => !Use24HourClock &&
        _timeSelectionPurpose is not (TimeSelectionPurpose.DayShift
            or TimeSelectionPurpose.MoveAmount) && !IsDurationPicker;
    public bool ShowQuickCapture => !IsAssistant && !IsSettings;
    public bool HasAttachment => !string.IsNullOrWhiteSpace(AttachedFileName);
    public bool IsAnyDatePickerOpen => IsEventDatePickerOpen || IsTaskDatePickerOpen;
    public bool IsEditingEvent => _editedEvent is not null;
    public bool IsEditingRecurringEvent => _editedEvent?.SeriesId is not null;
    public bool IsEditingTask => _editedTask is not null;
    public bool CanEditTaskBlock => _editedTaskBlock is { State: BlockState.Planned or BlockState.InProgress };
    public bool CanCompleteTaskBlock => _editedTaskBlock is { State: BlockState.Planned or BlockState.InProgress, TaskId: not null };
    public bool HasTaskBlockValidationError => !string.IsNullOrWhiteSpace(TaskBlockValidationMessage);
    public string TaskBlockStartTimeLabel => FormatClockTime(TaskBlockStartTime);
    public string TaskBlockEndTimeLabel => FormatClockTime(TaskBlockEndTime);
    public bool IsAutostartSupported => autostartService.IsSupported;
    public string TimeClockIdentifier => Use24HourClock ? "24HourClock" : "12HourClock";
    public string EventTimeLabel => FormatClockTime(EventTime);
    public string EventEndTimeLabel => FormatClockTime(EventEndTime);
    public string TimePickerTitle => localization.Text(_timeSelectionPurpose switch
    {
        TimeSelectionPurpose.DayShift => "ShiftSchedule",
        TimeSelectionPurpose.MoveAmount => "MoveByHowMuch",
        TimeSelectionPurpose.TaskDuration or TimeSelectionPurpose.ProjectDraftDuration
            or TimeSelectionPurpose.ProjectStepDuration or TimeSelectionPurpose.SittingDraftDuration
            or TimeSelectionPurpose.SittingStepDuration or TimeSelectionPurpose.FormDraftDuration => "Duration",
        TimeSelectionPurpose.EventStart or TimeSelectionPurpose.TaskBlockStart => "ChooseStartTime",
        _ => "ChooseEndTime"
    });
    public string AttachDialogTitle => localization.Text("AttachDialogTitle");
    public string AttachImagesFilterName => localization.Text("AttachImagesFilter");
    public string AttachDocumentsFilterName => localization.Text("AttachDocumentsFilter");
    public string CustomModelDialogTitle => localization.Text("CustomModelDialogTitle");
    public string TimePickerHourText
    {
        get
        {
            var value = TimePickerValue ?? TimeSpan.Zero;
            var hour = value.Hours;
            if (_timeSelectionPurpose is TimeSelectionPurpose.DayShift || IsDurationPicker)
                return ((int)value.TotalHours).ToString("00", localization.CurrentCulture);
            if (Use24HourClock) return hour.ToString("00", localization.CurrentCulture);
            return (hour % 12 is 0 ? 12 : hour % 12).ToString(localization.CurrentCulture);
        }
    }
    public string TimePickerMinuteText => (TimePickerValue ?? TimeSpan.Zero).Minutes.ToString("00", localization.CurrentCulture);
    public string TimePickerPeriodText
    {
        get
        {
            var isMorning = (TimePickerValue ?? TimeSpan.Zero).Hours < 12;
            var designator = isMorning
                ? localization.CurrentCulture.DateTimeFormat.AMDesignator
                : localization.CurrentCulture.DateTimeFormat.PMDesignator;
            return string.IsNullOrWhiteSpace(designator) ? (isMorning ? "AM" : "PM") : designator;
        }
    }
    public Avalonia.Media.IBrush EventAccent => EventVisuals.AccentBrush(Parse(SelectedEventCategoryOption?.Value ?? nameof(EventCategory.General), EventCategory.General));
    public Avalonia.Media.IBrush EventAccentGlow => EventVisuals.AccentGlowBrush(Parse(SelectedEventCategoryOption?.Value ?? nameof(EventCategory.General), EventCategory.General));
    public bool HasEventValidationError => !string.IsNullOrWhiteSpace(EventValidationMessage);
    /// <summary>
    /// Whether the chosen entry has an image encoder at all. A GGUF holds language weights and
    /// nothing else; seeing pictures takes a second file — the projector — that not every
    /// repository ships. Asking for images from a model that has none used to leave the engine
    /// permanently unstartable, waiting on a file that does not exist.
    /// </summary>
    public bool SelectedModelSupportsVision =>
        IsCustomModelSelected || LocalModelCatalog.Find(LocalModelId).SupportsVision;

    /// <summary>Image input, as far as this particular model can actually honour it.</summary>
    public bool IsVisionActive => LocalModelVisionEnabled && SelectedModelSupportsVision;

    /// <summary>The projector this model needs for images and has not got yet.</summary>
    public bool NeedsProjectorDownload => IsVisionActive && !IsCustomModelSelected && !IsSelectedProjectorInstalled;

    public bool NeedsLocalModelDownload =>
        !IsCustomModelSelected && (!IsSelectedLocalModelInstalled || NeedsProjectorDownload);
    public bool CanStartLocalModel => IsSelectedLocalModelInstalled && (!IsVisionActive || IsSelectedProjectorInstalled)
                                      && !IsLocalModelRunning && !IsLocalModelStarting && !IsLocalModelTransferring;
    public ObservableCollection<ChatModelQuickOptionViewModel> LocalModelQuickOptions { get; } = [];
    public ObservableCollection<ChatModelQuickOptionViewModel> ExternalModelQuickOptions { get; } = [];
    public bool IsLocalProviderActive => AssistantProvider == nameof(Domain.AssistantProvider.Local);
    public bool IsExternalProviderActive => AssistantProvider == nameof(Domain.AssistantProvider.OpenAiCompatible);
    public bool IsMcpProviderActive => AssistantProvider == nameof(Domain.AssistantProvider.Mcp);
    public void NotifyModelPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasInstalledLocalModels));
        OnPropertyChanged(nameof(LocalProviderMenuSubtitle));
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        OnPropertyChanged(nameof(ShowLocalModelResourceMonitor));
        OnPropertyChanged(nameof(CanToggleLocalModelExecution));
        OnPropertyChanged(nameof(CanStartLocalModel));
        OnPropertyChanged(nameof(CanStopLocalModel));
        OnPropertyChanged(nameof(RunningLocalModelDisplayName));
    }

    /// <summary>The model every runtime call is about.</summary>
    private LocalModelSelection Selection => new(LocalModelId);

    private LocalRuntimeOptions RuntimeOptions => new(
        int.TryParse(LocalModelContextText, out var context) ? Math.Clamp(context, 2048, 131_072) : 8192,
        int.TryParse(LocalModelThreadsText, out var threads) ? Math.Clamp(threads, 0, 256) : 0,
        IsVisionActive,
        ParseLocalModelIdleMinutes(),
        CustomModelPath,
        CustomProjectorPath,
        LocalModelGpuEnabled);

    public bool HasCustomModel => !string.IsNullOrWhiteSpace(CustomModelPath);
    public bool HasCustomProjector => !string.IsNullOrWhiteSpace(CustomProjectorPath);
    public string CustomModelFileName => HasCustomModel ? Path.GetFileName(CustomModelPath) : string.Empty;
    public string CustomProjectorFileName => HasCustomProjector ? Path.GetFileName(CustomProjectorPath) : string.Empty;
    public bool IsCustomModelSelected => LocalModelCatalog.IsCustom(LocalModelId);
    public bool ShowCatalogModelActions => IsLocalProviderActive && !IsCustomModelSelected;
    /// <summary>A user-supplied file is never downloaded, so the button hides for it.</summary>
    public bool CanDownloadLocalModel => !IsCustomModelSelected && NeedsLocalModelDownload;

    /// <summary>"Q5_K_M · ≈ 7.1 GB" for the chosen catalogue model, or the name of the user's own file.</summary>
    public string SelectedQuantLabel
    {
        get
        {
            if (IsCustomModelSelected) return CustomModelFileName;
            var quant = LocalModelCatalog.Find(LocalModelId).Quant;
            return $"{quant.Tag} · {quant.ApproximateSizeLabel}";
        }
    }

    public string SelectedLocalModelDisplayName => IsCustomModelSelected
        ? (HasCustomModel ? CustomModelFileName : localization.Text("CustomModelNotChosen"))
        : LocalModelCatalog.Find(LocalModelId).DisplayName;
    public string ActiveModelDisplayName => AssistantProvider switch
    {
        nameof(Domain.AssistantProvider.Local) => IsCustomModelSelected
            ? (HasCustomModel ? CustomModelFileName : localization.Text("LocalModelNotInstalled"))
            : HasInstalledLocalModels
                ? LocalModelCatalog.Find(LocalModelId).DisplayName
                : localization.Text("LocalModelNotInstalled"),
        nameof(Domain.AssistantProvider.OpenAiCompatible) => string.IsNullOrWhiteSpace(AssistantModel) ? localization.Text("OptionProviderOpenAi") : AssistantModel,
        nameof(Domain.AssistantProvider.Mcp) => "MCP",
        _ => localization.Text("OptionProviderOpenAi")
    };
    public bool CanToggleLocalModelExecution => CanStopLocalModel || CanStartLocalModel;
    public bool CanStopLocalModel => IsLocalModelRunning || IsLocalModelStarting;
    public string LocalModelExecutionTooltip => IsLocalModelRunning
        ? localization.Text("StopModel")
        : IsLocalModelStarting
            ? localization.Text("StartingLocalModel")
            : !IsSelectedLocalModelInstalled
                ? localization.Text("LocalModelNotInstalled")
                : localization.Text("StartModel");
    public bool CanDeleteLocalModel => (IsSelectedLocalModelInstalled || IsSelectedProjectorInstalled) && !IsLocalModelTransferring;
    public string LocalModelDownloadActionText => localization.Text(
        IsSelectedLocalModelInstalled ? "DownloadVisionSupport" : "DownloadModel");

    /// <summary>Whether a projector file of the user's own is worth asking for.</summary>
    public bool ShowProjectorPicker => IsCustomModelSelected && LocalModelVisionEnabled;

    /// <summary>One line saying what image input still needs, or that it is ready.</summary>
    public string VisionRequirementText
    {
        get
        {
            if (!LocalModelVisionEnabled) return string.Empty;
            if (!SelectedModelSupportsVision) return localization.Text("VisionUnsupported");
            if (IsCustomModelSelected)
                return localization.Text(HasCustomProjector ? "VisionReady" : "VisionNeedsCustomProjector");
            return localization.Text(IsSelectedProjectorInstalled ? "VisionReady" : "VisionNeedsProjector");
        }
    }
    public string TransparencyIntensityText => $"{TransparencyIntensity:0}%";
    /// <summary>Milliseconds read as seconds, because that is the unit the wait is felt in.</summary>
    public string WidgetHideDelayText =>
        $"{(WidgetHideDelayMs / 1000d).ToString("0.0", localization.CurrentCulture)} {localization.Text("SecondsShort")}";
    /// <summary>What the application actually waits, kept inside the range the slider offers.</summary>
    public TimeSpan WidgetHideDelay =>
        TimeSpan.FromMilliseconds(Math.Clamp(WidgetHideDelayMs, 400, 6000));
    public bool HasBlockingSurface => IsSettings || IsCalendarPopupOpen || IsAnalyticsOpen || IsChatOpen
                                      || IsEventComposerVisible || IsTaskEditorVisible || IsTaskBlockEditorVisible
                                      || IsMovePanelOpen
                                      || IsAnyDatePickerOpen || IsTimePickerOpen;
    /// <summary>The assistant as a sheet over the planner: open, and not moved out to its own window.</summary>
    public bool IsChatSheetOpen => IsChatOpen && !IsChatExpanded;
    /// <summary>The assistant as a panel of its own, standing beside the shell like the other three.</summary>
    public bool IsChatDocked => IsChatOpen && IsChatExpanded;
    public string ChatExpandTooltip => localization.Text(IsChatExpanded ? "ChatCollapse" : "ChatExpand");
    /// <summary>
    /// Whether one of the panels that stand beside the shell is up. The assistant joins them the
    /// moment it is widened, and leaves them again when it folds back into the planner.
    /// </summary>
    public bool IsShellFlyoutOpen => IsSettings || IsCalendarPopupOpen || IsAnalyticsOpen || IsChatDocked;

    public event EventHandler? ShellSettingsChanged;
    public event EventHandler? LocalizationChanged;

    [RelayCommand]
    private void ToggleModelMenu()
    {
        IsModelMenuOpen = !IsModelMenuOpen;
        if (IsModelMenuOpen)
        {
            IsLocalModelsSubMenu = false;
            IsSwitchModelConfirmationOpen = false;
        }
    }

    [RelayCommand]
    private void OpenLocalModelsSubMenu()
    {
        IsLocalModelsSubMenu = true;
        IsSwitchModelConfirmationOpen = false;
    }

    [RelayCommand]
    private void CloseLocalModelsSubMenu()
    {
        IsLocalModelsSubMenu = false;
        IsSwitchModelConfirmationOpen = false;
    }

    [RelayCommand]
    private void CloseModelMenu()
    {
        IsModelMenuOpen = false;
        IsLocalModelsSubMenu = false;
        IsSwitchModelConfirmationOpen = false;
    }

    [RelayCommand]
    private void ToggleLocalModelsExpanded() => IsLocalModelsExpanded = !IsLocalModelsExpanded;

    [RelayCommand]
    private void OpenLocalAiSettings()
    {
        AssistantProvider = nameof(Domain.AssistantProvider.Local);
        SelectedAssistantProviderOption = AssistantProviderOptions.FirstOrDefault(o => o.Value == AssistantProvider);
        IsModelMenuOpen = false;
        IsLocalModelsSubMenu = false;
        IsSwitchModelConfirmationOpen = false;
        IsChatOpen = false;
        SettingsSection = "models";
        ShowSettings();
    }

    [RelayCommand]
    private void SelectLocalModelById(string? modelId)
    {
        var desc = LocalModelCatalog.Find(modelId);
        RequestSwitchModel(desc.Id, nameof(Domain.AssistantProvider.Local));
    }

    [RelayCommand]
    private void SelectExternalApi()
    {
        RequestSwitchModel("openai", nameof(Domain.AssistantProvider.OpenAiCompatible));
    }

    [RelayCommand]
    private void SelectMcpProvider()
    {
        RequestSwitchModel("mcp", nameof(Domain.AssistantProvider.Mcp));
    }

    private void RequestSwitchModel(string targetId, string targetProvider)
    {
        var isLocal = targetProvider == nameof(Domain.AssistantProvider.Local);
        var canonicalTargetId = isLocal ? LocalModelCatalog.Find(targetId).Id : targetId;

        if (IsLocalModelRunning || IsLocalModelStarting)
        {
            var runningModelId = localModels.Status.ModelId;
            if (isLocal && canonicalTargetId.Equals(runningModelId, StringComparison.OrdinalIgnoreCase))
            {
                IsSwitchModelConfirmationOpen = false;
                IsModelMenuOpen = false;
                IsLocalModelsSubMenu = false;
                return;
            }

            var runningModelDesc = LocalModelCatalog.Find(runningModelId);
            SwitchModelTargetId = canonicalTargetId;
            SwitchModelTargetProvider = targetProvider;
            SwitchModelIsTargetLocal = isLocal;
            SwitchModelIsTargetInstalled = isLocal && localModels.GetStatus(
                new LocalModelSelection(canonicalTargetId),
                CustomModelPath).ModelInstalled;
            SwitchModelConfirmationTitle = localization.Text("SwitchModelConfirmTitle");

            if (isLocal)
            {
                var targetModelDesc = LocalModelCatalog.Find(canonicalTargetId);
                SwitchModelConfirmationMessage = string.Format(localization.Text("SwitchModelConfirmText"), runningModelDesc.DisplayName, targetModelDesc.DisplayName);
            }
            else
            {
                var providerName = targetProvider == nameof(Domain.AssistantProvider.Mcp) ? "MCP" : localization.Text("OptionProviderOpenAi");
                SwitchModelConfirmationMessage = string.Format(localization.Text("SwitchProviderConfirmText"), runningModelDesc.DisplayName, providerName);
            }

            IsSwitchModelConfirmationOpen = true;
            return;
        }

        ApplyModelSwitch(canonicalTargetId, targetProvider);
    }

    private void ApplyModelSwitch(string targetId, string targetProvider)
    {
        if (targetProvider == nameof(Domain.AssistantProvider.Local))
        {
            AssistantProvider = nameof(Domain.AssistantProvider.Local);
            LocalModelId = LocalModelCatalog.IsCustom(targetId) ? LocalModelCatalog.CustomModelId : LocalModelCatalog.Find(targetId).Id;
            ApplyLocalModelStatus(localModels.GetStatus(Selection, CustomModelPath));
        }
        else
        {
            AssistantProvider = targetProvider;
        }

        SelectedAssistantProviderOption = AssistantProviderOptions.FirstOrDefault(o => o.Value == AssistantProvider);
        ScheduleSettingsSave();
        RefreshChatModelQuickOptions();
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        OnPropertyChanged(nameof(IsLocalProviderActive));
        OnPropertyChanged(nameof(IsExternalProviderActive));
        OnPropertyChanged(nameof(IsMcpProviderActive));
        OnPropertyChanged(nameof(SelectedLocalModelDisplayName));
        OnPropertyChanged(nameof(LocalProviderMenuSubtitle));
        IsSwitchModelConfirmationOpen = false;
        IsModelMenuOpen = false;
        IsLocalModelsSubMenu = false;
    }

    [RelayCommand]
    private async Task ConfirmSwitchAndStartModelAsync()
    {
        IsSwitchModelConfirmationOpen = false;
        var targetId = SwitchModelTargetId;
        var targetProvider = SwitchModelTargetProvider;
        var shouldStart = SwitchModelIsTargetLocal && SwitchModelIsTargetInstalled;

        await StopLocalModelAsync();

        if (!string.IsNullOrWhiteSpace(targetId) && !string.IsNullOrWhiteSpace(targetProvider))
        {
            ApplyModelSwitch(targetId, targetProvider);
            if (shouldStart)
            {
                await StartLocalModelAsync();
            }
        }
    }

    [RelayCommand]
    private async Task ConfirmSwitchOnlyModelAsync()
    {
        IsSwitchModelConfirmationOpen = false;
        var targetId = SwitchModelTargetId;
        var targetProvider = SwitchModelTargetProvider;

        await StopLocalModelAsync();

        if (!string.IsNullOrWhiteSpace(targetId) && !string.IsNullOrWhiteSpace(targetProvider))
        {
            ApplyModelSwitch(targetId, targetProvider);
        }
    }

    [RelayCommand]
    private void ConfirmSwitchKeepRunning()
    {
        IsSwitchModelConfirmationOpen = false;
        var targetId = SwitchModelTargetId;
        var targetProvider = SwitchModelTargetProvider;

        if (!string.IsNullOrWhiteSpace(targetId) && !string.IsNullOrWhiteSpace(targetProvider))
        {
            ApplyModelSwitch(targetId, targetProvider);
        }
    }

    /// <summary>
    /// Picking the engine from a card rather than a dropdown. The three choices are what the
    /// whole AI section reshapes around, so they are worth showing rather than hiding in a list.
    /// </summary>
    [RelayCommand]
    private void SelectAssistantProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        var option = AssistantProviderOptions.FirstOrDefault(item => item.Value == provider);
        if (option is null) return;
        SelectedAssistantProviderOption = option;
    }

    /// <summary>One line under the provider cards saying what the current choice implies.</summary>
    public string AssistantProviderSummary => localization.Text(AssistantProvider switch
    {
        nameof(Domain.AssistantProvider.OpenAiCompatible) => "AiProviderApiSummary",
        nameof(Domain.AssistantProvider.Mcp) => "AiProviderMcpSummary",
        _ => "AiProviderLocalSummary"
    });

    [RelayCommand]
    private void CancelSwitchModelConfirmation()
    {
        IsSwitchModelConfirmationOpen = false;
        SwitchModelTargetId = null;
        SwitchModelTargetProvider = null;
    }

    partial void OnAssistantProviderChanged(string value)
    {
        OnPropertyChanged(nameof(ShowApiKey));
        OnPropertyChanged(nameof(ShowApiSettings)); OnPropertyChanged(nameof(ShowMcpSettings)); OnPropertyChanged(nameof(ShowStdioSettings)); OnPropertyChanged(nameof(ShowModel));
        OnPropertyChanged(nameof(ShowMcpHttpSettings));
        OnPropertyChanged(nameof(ShowSamplingParameters));
        OnPropertyChanged(nameof(ShowToolLoopParameters));
        OnPropertyChanged(nameof(ShowMcpAdvancedNotice));
        // Each engine has its own request settings; the page shows the set of the one answering.
        OnPropertyChanged(nameof(ActiveParameters));
        OnPropertyChanged(nameof(ActiveParametersHint));
        OnPropertyChanged(nameof(ShowLocalModelSettings));
        OnPropertyChanged(nameof(ShowExternalContextConsent));
        OnPropertyChanged(nameof(AssistantProviderSummary));
        OnPropertyChanged(nameof(CanSendImages));
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        OnPropertyChanged(nameof(IsLocalProviderActive));
        OnPropertyChanged(nameof(IsExternalProviderActive));
        OnPropertyChanged(nameof(IsMcpProviderActive));
        OnPropertyChanged(nameof(SelectedLocalModelDisplayName));
        OnPropertyChanged(nameof(ShowLocalModelResourceMonitor));
        RefreshChatModelQuickOptions();
        ScheduleSettingsSave();
    }
    partial void OnMcpTransportChanged(string value)
    {
        OnPropertyChanged(nameof(ShowMcpHttpSettings));
        OnPropertyChanged(nameof(ShowStdioSettings));
        RefreshChatModelQuickOptions();
        ScheduleSettingsSave();
    }
    partial void OnThemeColorSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomColor));
        if (value != nameof(Domain.ThemeColorSource.Custom))
        {
            IsAccentColorPickerOpen = false;
            IsBackgroundColorPickerOpen = false;
        }
        if (!_loadingSettings && value == nameof(Domain.ThemeColorSource.Custom)) SaveActiveThemeColors();
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }
    partial void OnShellThemeChanged(string value)
    {
        ChangeThemeColorContext();
    }
    partial void OnShellThemeModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDarkThemeMode));
        OnPropertyChanged(nameof(IsLightThemeMode));
        ChangeThemeColorContext();
    }
    partial void OnVisualPaletteChanged(string value) { PreviewCurrentTheme(); ScheduleSettingsSave(); }
    partial void OnCustomAccentChanged(string value)
    {
        SynchronizePickerColor(value, true);
        OnPropertyChanged(nameof(CustomAccentBrush));
        if (_changingThemeColors) return;
        ChangeLegacyAppearanceColor(ThemeColorRole.Accent, value);
        ScheduleThemeColorPreview();
    }
    partial void OnCustomBackgroundChanged(string value)
    {
        SynchronizePickerColor(value, false);
        OnPropertyChanged(nameof(CustomBackgroundBrush));
        if (_changingThemeColors) return;
        ChangeLegacyAppearanceColor(ThemeColorRole.Background, value);
        ScheduleThemeColorPreview();
    }
    partial void OnCustomAccentColorChanged(Color value) => ApplyPickerColor(value, true);
    partial void OnCustomBackgroundColorChanged(Color value) => ApplyPickerColor(value, false);
    partial void OnMascotColorChanged(string value)
    {
        SynchronizeMascotColor(value);
        NotifyMascotColor();
        ScheduleSettingsSave();
    }
    partial void OnMascotColorValueChanged(Color value)
    {
        if (!_synchronizingMascotColor)
        {
            _synchronizingMascotColor = true;
            try { MascotColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}"; }
            finally { _synchronizingMascotColor = false; }
        }
        NotifyMascotColor();
        ScheduleSettingsSave();
    }
    partial void OnShowMascotChanged(bool value)
    {
        // A creature nobody is drawing has no colour to choose, so the picker goes with it.
        if (!value) IsMascotColorPickerOpen = false;
        OnPropertyChanged(nameof(PeekColumnWidth));
        ScheduleSettingsSave();
    }

    partial void OnUseThemeMascotColorChanged(bool value)
    {
        // Handing the user a colour to adjust rather than an arbitrary one: whatever the theme
        // is painting the creature with right now is where the picker starts.
        if (!value && string.IsNullOrWhiteSpace(MascotColor)) MascotColor = CurrentAccentHex();
        if (value) IsMascotColorPickerOpen = false;
        NotifyMascotColor();
        ScheduleSettingsSave();
    }

    private void NotifyMascotColor()
    {
        OnPropertyChanged(nameof(MascotColorBrush));
        OnPropertyChanged(nameof(MascotAccentBrush));
    }

    private string CurrentAccentHex()
    {
        var defaults = themeService.GetDefaults(
            Parse(ShellTheme, Domain.ShellTheme.Marelian),
            Parse(ShellThemeMode, Domain.ShellThemeMode.Dark));
        return IsCustomColor ? NormalizeColor(CustomAccent, defaults.Accent) : defaults.Accent;
    }

    private void SynchronizeMascotColor(string value)
    {
        if (_synchronizingMascotColor) return;
        Color parsed;
        try { parsed = Color.Parse(value); }
        catch { return; }

        _synchronizingMascotColor = true;
        try { if (MascotColorValue != parsed) MascotColorValue = parsed; }
        finally { _synchronizingMascotColor = false; }
    }
    partial void OnEnableTransparencyChanged(bool value)
    {
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }
    partial void OnTransparencyIntensityChanged(double value)
    {
        OnPropertyChanged(nameof(TransparencyIntensityText));
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }
    partial void OnRevealModeChanged(string value) => ScheduleSettingsSave();
    partial void OnWidgetHideDelayMsChanged(double value)
    {
        OnPropertyChanged(nameof(WidgetHideDelayText));
        OnPropertyChanged(nameof(WidgetHideDelay));
        ScheduleSettingsSave();
    }
    partial void OnWidgetKeepOpenChanged(bool value) => ScheduleSettingsSave();
    partial void OnPinOpenChanged(bool value) => ScheduleSettingsSave();
    partial void OnStartHiddenChanged(bool value) => ScheduleSettingsSave();
    partial void OnKeepOnTopChanged(bool value) => ScheduleSettingsSave();
    partial void OnUse24HourClockChanged(bool value)
    {
        OnPropertyChanged(nameof(TimeClockIdentifier));
        OnPropertyChanged(nameof(EventTimeLabel));
        OnPropertyChanged(nameof(EventEndTimeLabel));
        OnPropertyChanged(nameof(TaskBlockStartTimeLabel));
        OnPropertyChanged(nameof(TaskBlockEndTimeLabel));
        NotifyTimePickerDisplay();
        RefreshClock();
        RebuildSchedulerHourLabels();
        if (!_loadingSettings) _ = ReloadLocalizedStateAsync();
        ScheduleSettingsSave();
    }
    partial void OnIsDayShiftEarlierChanged(bool value) => OnPropertyChanged(nameof(IsDayShiftLater));

    partial void OnSelectedEventCategoryOptionChanged(ChoiceOption? value)
    {
        if (value is not null)
            _eventCategoryDraft = Parse(value.Value, EventCategory.General);
        OnPropertyChanged(nameof(EventAccent));
        OnPropertyChanged(nameof(EventAccentGlow));
    }

    partial void OnSelectedEventPriorityOptionChanged(ChoiceOption? value)
    {
        if (value is not null)
            _eventPriorityDraft = Parse(value.Value, EventPriority.Normal);
    }

    partial void OnSelectedEventRecurrenceOptionChanged(ChoiceOption? value)
    {
        if (value is null) return;
        _eventRecurrenceDraft = Parse(value.Value, EventRecurrence.None);
        if (_editedEvent?.SeriesId is not null && value?.Value != _editedEvent.Recurrence.ToString())
            SelectedEventEditScopeOption = EventEditScopeOptions.FirstOrDefault(option => option.Value == nameof(EventSeriesScope.ThisAndFollowing));
    }
    partial void OnEventTitleChanged(string value) => ClearEventValidation();
    partial void OnEventDateChanged(DateTimeOffset? value) => ClearEventValidation();
    partial void OnEventTimeChanged(TimeSpan? value) { OnPropertyChanged(nameof(EventTimeLabel)); ClearEventValidation(); RefreshEventDraftSummary(); }
    partial void OnEventEndTimeChanged(TimeSpan? value) { OnPropertyChanged(nameof(EventEndTimeLabel)); ClearEventValidation(); RefreshEventDraftSummary(); }
    partial void OnEditDurationTextChanged(string value) => OnPropertyChanged(nameof(TaskDurationLabel));
    partial void OnTimePickerValueChanged(TimeSpan? value) => NotifyTimePickerDisplay();
    partial void OnIsEditingStartTimeChanged(bool value) => OnPropertyChanged(nameof(TimePickerTitle));
    partial void OnEventValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasEventValidationError));
    partial void OnTaskBlockValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasTaskBlockValidationError));
    partial void OnTaskBlockStartTimeChanged(TimeSpan? value) => OnPropertyChanged(nameof(TaskBlockStartTimeLabel));
    partial void OnTaskBlockEndTimeChanged(TimeSpan? value) => OnPropertyChanged(nameof(TaskBlockEndTimeLabel));
    partial void OnReduceMotionChanged(bool value) => ScheduleSettingsSave();

    /// <summary>
    /// The engine is launched with the choice baked in, so a model already up has to be brought
    /// down for the new answer to mean anything. It is restarted only if it was running.
    /// </summary>
    partial void OnLocalModelGpuEnabledChanged(bool value)
    {
        ScheduleSettingsSave();
        if (IsLocalModelRunning || localModels.Status.State == LocalModelRuntimeState.Running)
            RestartLocalModelCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RestartLocalModelAsync()
    {
        try
        {
            await localModels.StopAsync();
            await localModels.EnsureReadyAsync(Selection, RuntimeOptions);
            Status = localization.Text("LocalModelRunning");
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Restarting local model");
            Status = exception.Message;
        }
    }
    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_loadingSettings || _synchronizingAutostartState) return;
        var revision = Interlocked.Increment(ref _autostartChangeVersion);
        _ = ApplyAutostartAsync(value, revision);
    }
    partial void OnAssistantEndpointChanged(string value) => ScheduleSettingsSave();
    partial void OnAssistantAccessLevelChanged(string value)
    {
        SelectedAssistantAccessLevelOption = AssistantAccessLevelOptions.FirstOrDefault(option => option.Value == value);
        OnPropertyChanged(nameof(AssistantAccessLevelLabel));
        OnPropertyChanged(nameof(AssistantAccessLevelDescription));
        OnPropertyChanged(nameof(IsAccessConfirmAll));
        OnPropertyChanged(nameof(IsAccessAutoSafe));
        OnPropertyChanged(nameof(IsAccessFullAccess));
        ScheduleSettingsSave();
    }

    [RelayCommand]
    private void SetAssistantAccess(string? level)
    {
        if (!string.IsNullOrWhiteSpace(level)) AssistantAccessLevel = level;
    }
    partial void OnShareContextWithExternalAiChanged(bool value) => ScheduleSettingsSave();
    partial void OnAssistantModelChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        RefreshChatModelQuickOptions();
        ScheduleSettingsSave();
    }
    partial void OnMcpEndpointChanged(string value) => ScheduleSettingsSave();
    partial void OnMcpCommandChanged(string value) => ScheduleSettingsSave();
    partial void OnMcpArgumentsChanged(string value) => ScheduleSettingsSave();
    partial void OnMcpToolNameChanged(string value) => ScheduleSettingsSave();
    partial void OnMcpToolsEnabledChanged(bool value) => ScheduleSettingsSave();
    partial void OnAssistantMemoryEnabledChanged(bool value) => ScheduleSettingsSave();
    partial void OnLocalModelIdChanged(string value)
    {
        foreach (var option in LocalModelOptions) option.IsSelected = option.Id == value;
        ApplyLocalModelStatus(localModels.GetStatus(Selection, CustomModelPath));
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        RefreshChatModelQuickOptions();
        RetuneLocalModel();
        ScheduleSettingsSave();
    }
    partial void OnLocalModelVisionEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsLocalModelDownload));
        OnPropertyChanged(nameof(CanStartLocalModel));
        OnPropertyChanged(nameof(LocalModelDownloadActionText));
        OnPropertyChanged(nameof(CanSendImages));
        // The projector is loaded beside the weights and needs room of its own.
        RetuneLocalModel();
        ScheduleSettingsSave();
    }
    partial void OnSelectedLocalModelIdleOptionChanged(ChoiceOption? value) => ScheduleSettingsSave();
    partial void OnLocalModelContextTextChanged(string value) => ScheduleSettingsSave();
    partial void OnLocalModelThreadsTextChanged(string value) => ScheduleSettingsSave();
    partial void OnCustomModelPathChanged(string value)
    {
        ApplyLocalModelStatus(localModels.GetStatus(Selection, value));
        RetuneLocalModel();
        ScheduleSettingsSave();
    }
    partial void OnCustomProjectorPathChanged(string value) => ScheduleSettingsSave();
    partial void OnAssistantExtraInstructionsChanged(string value) => ScheduleSettingsSave();
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null || _loadingSettings) return;
        localization.Apply(value.Code);
        BuildLocalizedChoices();
        RefreshLocalizedState();
        LocalizationChanged?.Invoke(this, EventArgs.Empty);
        ScheduleSettingsSave();
    }
    partial void OnSelectedThemeOptionChanged(ChoiceOption? value) { if (value is not null) ShellTheme = value.Value; }
    partial void OnSelectedThemeModeOptionChanged(ChoiceOption? value) { if (value is not null) ShellThemeMode = value.Value; }
    partial void OnSelectedColorSourceOptionChanged(ChoiceOption? value) { if (value is not null) ThemeColorSource = value.Value; }
    partial void OnSelectedVisualPaletteOptionChanged(ChoiceOption? value) { if (value is not null) VisualPalette = value.Value; }
    partial void OnSelectedFontOptionChanged(ChoiceOption? value)
    {
        if (value is null || AppFontFamily == value.Value) return;
        AppFontFamily = value.Value;
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }
    partial void OnSelectedAssistantProviderOptionChanged(ChoiceOption? value) { if (value is not null) AssistantProvider = value.Value; }
    partial void OnSelectedAssistantAccessLevelOptionChanged(ChoiceOption? value) { if (value is not null) AssistantAccessLevel = value.Value; OnPropertyChanged(nameof(AssistantAccessLevelLabel)); OnPropertyChanged(nameof(AssistantAccessLevelDescription)); }
    partial void OnSelectedMcpTransportOptionChanged(ChoiceOption? value) { if (value is not null) McpTransport = value.Value; }
    partial void OnIsCalendarPopupOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasBlockingSurface));
        OnPropertyChanged(nameof(IsShellFlyoutOpen));
        // The analytics may have handed this panel its place and left its charts standing for
        // the switch back. Closing it outright is the end of that hand-off.
        if (!value) CollapseAnalyticsChartsIfUnreachable();
    }
    partial void OnIsAnalyticsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasBlockingSurface));
        OnPropertyChanged(nameof(IsShellFlyoutOpen));
        // Building the report is what opening this window costs. Started here it runs inside
        // the property setter — before the window that asked for it has even been told to show
        // — so the panel arrived late every time. The report is now built on a worker while the
        // window comes in, and only laid out into shapes once it has landed, exactly as the
        // calendar's own refresh does.
        //
        // A panel taking another panel's place waits longer, because it waits for two moves: the
        // one in front of it folding back into the edge, and then its own unfolding out of it.
        // Laid out at the old moment the report landed in the middle of that and the switch
        // stuttered on it. The flag is left standing either way: the rebuild reads it too, to
        // decide whether the charts have an arrival to play, and clears it once it has.
        if (value) Dispatcher.UIThread.Post(() =>
        {
            var settled = AnalyticsTakesAnotherPanelsPlace ? SwapSettled() : RevealSettled();
            AnalyticsTakesAnotherPanelsPlace = false;
            _ = RefreshAnalyticsAsync(settled);
        });
        // Leaving takes the charts with it. They used to stay standing behind a closed panel,
        // which is what made the next opening show a finished chart, throw it away and draw it
        // again in front of the reader.
        else CollapseAnalyticsCharts();
    }
    partial void OnIsChatOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasBlockingSurface));
        OnPropertyChanged(nameof(IsShellFlyoutOpen));
        RefreshChatSheetPresence();
        // The assistant sheet also switches the analytics off without going through its
        // property, so it inherits the same standing charts and the same duty to put them away —
        // both when it takes their place and when it hands that place back to nothing. Asked
        // either way round: the call does nothing at all while any panel is still up.
        CollapseAnalyticsChartsIfUnreachable();
    }
    /// <summary>
    /// Widening moves the conversation from a sheet inside the planner to a panel beside it,
    /// which is the same move the calendar and the analytics make — so it joins and leaves the
    /// side of the shell on exactly their terms.
    /// </summary>
    partial void OnIsChatExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShellFlyoutOpen));
        OnPropertyChanged(nameof(ChatBodyFontSize));
        RefreshChatSheetPresence();
        CollapseAnalyticsChartsIfUnreachable();
    }

    /// <summary>
    /// Keeps <see cref="IsChatSheetPresent"/> a step behind <see cref="IsChatSheetOpen"/> on the
    /// way down only: up is immediate, because nothing can arrive before it exists.
    /// </summary>
    private void RefreshChatSheetPresence()
    {
        _chatSheetLeaving?.Cancel();
        _chatSheetLeaving?.Dispose();
        _chatSheetLeaving = null;
        if (IsChatSheetOpen) { IsChatSheetPresent = true; return; }
        if (!IsChatSheetPresent) return;
        // Nothing to wait for when there is nothing to watch: with motion off the sheet has no
        // leaving to play, and lingering is then just a sheet that takes a moment to go away.
        if (ReduceMotion) { IsChatSheetPresent = false; return; }
        var leaving = new CancellationTokenSource();
        _chatSheetLeaving = leaving;
        _ = RetireChatSheetAsync(leaving.Token);
    }

    private async Task RetireChatSheetAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ChatSheetLeaveDuration, token);
            if (!token.IsCancellationRequested && !IsChatSheetOpen) IsChatSheetPresent = false;
        }
        catch (OperationCanceledException) { }
    }
    partial void OnIsEventDatePickerOpenChanged(bool value) { OnPropertyChanged(nameof(IsAnyDatePickerOpen)); OnPropertyChanged(nameof(HasBlockingSurface)); RefreshDatePickerClear(); }
    partial void OnIsTaskDatePickerOpenChanged(bool value) { OnPropertyChanged(nameof(IsAnyDatePickerOpen)); OnPropertyChanged(nameof(HasBlockingSurface)); RefreshDatePickerClear(); }
    /// <summary>Whether the date picker offers "no day" depends on what it was opened for this time.</summary>
    private void RefreshDatePickerClear()
    {
        OnPropertyChanged(nameof(CanClearCalendarDate));
        OnPropertyChanged(nameof(CalendarClearLabel));
    }
    partial void OnIsTimePickerOpenChanged(bool value) => OnPropertyChanged(nameof(HasBlockingSurface));
    partial void OnAttachedFileNameChanged(string value) => OnPropertyChanged(nameof(HasAttachment));
    partial void OnIsEventComposerVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(HasBlockingSurface));
        // A composer put away without saving books nothing for the step it was opened for.
        if (!value) _schedulingStepId = null;
    }
    partial void OnIsTaskEditorVisibleChanged(bool value) => OnPropertyChanged(nameof(HasBlockingSurface));
    partial void OnIsTaskBlockEditorVisibleChanged(bool value) => OnPropertyChanged(nameof(HasBlockingSurface));
    partial void OnIsSettingsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasBlockingSurface));
        OnPropertyChanged(nameof(IsShellFlyoutOpen));
        if (value)
        {
            if (IsSettingsData) _ = RefreshCloudStatusAsync();
            return;
        }
        CollapseAnalyticsChartsIfUnreachable();
        // The shortcuts page is on this panel, and a row left listening holds every global claim
        // open: they are dropped for as long as one is waiting for a combination. The window's
        // own Deactivated answers for a page the user clicked away from — this answers for a
        // panel that was closed out from under it, which is what the shell's own shortcut does.
        CancelShortcutCapture();
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasDynamicSuggestions));
    partial void OnCalendarDateChanged(DateTime? value)
    {
        if (value is not { } date) return;
        _ = SelectDateSafelyAsync(DateOnly.FromDateTime(date));
    }

    public async Task InitializeAsync()
    {
        _settings = await settingsRepository.GetAppSettingsAsync();
        var isFirstLanguageSelection = string.IsNullOrWhiteSpace(_settings.LanguageCode);
        _settings.LanguageCode = localization.ResolveLanguage(_settings.LanguageCode);
        localization.Apply(_settings.LanguageCode);
        LoadSettings(_settings);
        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        _resourceMonitorTimer.Tick += (_, _) => UpdateResourceMetrics();
        _resourceMonitorTimer.Start();
        RefreshClock();
        BuildLocalizedChoices();
        if (!_localModelEventsAttached)
        {
            localModels.StatusChanged += LocalModelsOnStatusChanged;
            _localModelEventsAttached = true;
        }
        await localModels.RefreshAsync(Selection);
        themeService.Apply(_settings);
        _loadingSettings = false;
        if (isFirstLanguageSelection)
            await settingsRepository.UpdateAppSettingsAsync(_settings);
        SetSection(_settings.LastSection, persist: false);
        AssistantState = localization.Text("AssistantPrivacy");
        ComposerPlaceholder = localization.Text("AssistantPlaceholder");
        await InitializeChatAsync();
        await ReloadAsync();
        RefreshDateLabels();
        await RefreshCalendarAsync();
        SettingsSaveState = localization.Text("SettingsSaved");
        await SynchronizeAutostartAsync();
        StartBackgroundChores();
    }

    public void PreviewTheme(Domain.ShellTheme theme)
    {
        if (_settings is null) return;
        _settings.ShellTheme = Domain.ShellTheme.Marelian;
        ShellTheme = nameof(Domain.ShellTheme.Marelian);
        themeService.Apply(_settings);
    }

    public void PreviewLanguage(string languageCode)
    {
        var option = LanguageOptions.FirstOrDefault(language => language.Code.Equals(languageCode, StringComparison.OrdinalIgnoreCase));
        if (option is not null) SelectedLanguage = option;
    }

    [RelayCommand]
    private void SelectThemeMode(string? mode)
    {
        if (Enum.TryParse<Domain.ShellThemeMode>(mode, true, out var parsed))
        {
            ShellThemeMode = parsed.ToString();
            SelectedThemeModeOption = ThemeModeOptions.FirstOrDefault(option => option.Value == ShellThemeMode);
        }
    }

    [RelayCommand]
    private void ToggleAccentColorPicker()
    {
        IsAccentColorPickerOpen = !IsAccentColorPickerOpen;
        if (IsAccentColorPickerOpen)
        {
            IsBackgroundColorPickerOpen = false;
            IsMascotColorPickerOpen = false;
        }
    }

    [RelayCommand]
    private void ToggleBackgroundColorPicker()
    {
        IsBackgroundColorPickerOpen = !IsBackgroundColorPickerOpen;
        if (IsBackgroundColorPickerOpen)
        {
            IsAccentColorPickerOpen = false;
            IsMascotColorPickerOpen = false;
        }
    }

    [RelayCommand]
    private void ToggleMascotColorPicker()
    {
        IsMascotColorPickerOpen = !IsMascotColorPickerOpen;
        if (!IsMascotColorPickerOpen) return;
        foreach (var editor in ThemeColors) editor.IsExpanded = false;
        IsAccentColorPickerOpen = false;
        IsBackgroundColorPickerOpen = false;
    }

    [RelayCommand]
    private void ResetThemeColors()
    {
        var mode = Parse(ShellThemeMode, Domain.ShellThemeMode.Dark);
        var defaults = themeService.GetDefaults(Domain.ShellTheme.Marelian, mode);
        _changingThemeColors = true;
        try { CustomAccent = defaults.Accent; CustomBackground = defaults.Background; }
        finally { _changingThemeColors = false; }
        LoadAppearanceControls(new());
        // Accent source is shared by both modes; resetting one variant keeps that preference.
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }

    private void ChangeThemeColorContext()
    {
        if (_loadingSettings) return;
        SaveActiveThemeColors();
        var mode = Parse(ShellThemeMode, Domain.ShellThemeMode.Dark);
        _activeThemeColorKey = MarelianAppearance.Key(mode);
        var defaults = themeService.GetDefaults(Domain.ShellTheme.Marelian, mode);
        var profile = _themeColorOverrides.GetValueOrDefault(_activeThemeColorKey) ?? new();
        _changingThemeColors = true;
        try { CustomAccent = profile.Accent ?? defaults.Accent; CustomBackground = profile.Background ?? defaults.Background; }
        finally { _changingThemeColors = false; }
        LoadAppearanceControls(profile);
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }

    private void SaveActiveThemeColors()
    {
        if (_loadingSettings || _changingThemeColors || _activeThemeColorKey is null) return;
        _themeColorOverrides[_activeThemeColorKey] = _appearanceProfile.Normalize();
    }

    private void LoadThemeColorOverrides(AppSettings settings)
    {
        MarelianAppearance.NormalizeSettings(settings);
        _themeColorOverrides.Clear();
        foreach (var pair in MarelianAppearance.Read(settings.ThemeColorOverridesJson)) _themeColorOverrides[pair.Key] = pair.Value;
        _activeThemeColorKey = MarelianAppearance.Key(settings.ShellThemeMode);
        var profile = _themeColorOverrides.GetValueOrDefault(_activeThemeColorKey) ?? new();
        var defaults = themeService.GetDefaults(Domain.ShellTheme.Marelian, settings.ShellThemeMode);
        settings.CustomAccent = profile.Accent ?? defaults.Accent;
        settings.CustomBackground = profile.Background ?? defaults.Background;
    }

    private void ApplyPickerColor(Color value, bool accent)
    {
        if (_synchronizingColorValues) return;
        _synchronizingColorValues = true;
        try
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (accent) CustomAccent = hex;
            else CustomBackground = hex;
        }
        finally { _synchronizingColorValues = false; }

        if (!_loadingSettings && ThemeColorSource != nameof(Domain.ThemeColorSource.Custom))
        {
            ThemeColorSource = nameof(Domain.ThemeColorSource.Custom);
            SelectedColorSourceOption = ColorSourceOptions.FirstOrDefault(option => option.Value == ThemeColorSource);
        }
    }

    private void SynchronizePickerColor(string value, bool accent)
    {
        if (_synchronizingColorValues) return;
        Color parsed;
        try { parsed = Color.Parse(value); }
        catch { return; }

        _synchronizingColorValues = true;
        try
        {
            if (accent)
            {
                if (CustomAccentColor != parsed) CustomAccentColor = parsed;
            }
            else
            {
                if (CustomBackgroundColor != parsed) CustomBackgroundColor = parsed;
            }
        }
        finally { _synchronizingColorValues = false; }
    }

    /// <summary>Coalesces spectrum pointer moves to a smooth 25 FPS theme preview.</summary>
    private void ScheduleThemeColorPreview()
    {
        if (_loadingSettings || _themeColorPreviewPending) return;
        _themeColorPreviewPending = true;
        _ = ApplyThemeColorPreviewAsync();
    }

    private async Task ApplyThemeColorPreviewAsync()
    {
        await Task.Delay(40);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _themeColorPreviewPending = false;
            SaveActiveThemeColors();
            PreviewCurrentTheme();
            ScheduleSettingsSave();
        });
    }

    public void ReportUnexpectedError(Exception exception)
    {
        IsBusy = false;
        Status = exception.Message;
        AssistantState = exception.Message;
    }

    private void PreviewCurrentTheme()
    {
        if (_loadingSettings || _changingThemeColors || _settings is null) return;
        CopySettingsFromView(includeApiKey: false);
        themeService.Apply(_settings);
        ShellSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void ShowHome() => SetSection("home");
    [RelayCommand] private void ShowTasks() { IsTasksExpanded = true; SetSection("plan"); }
    [RelayCommand] private void ShowCalendar() => OpenCalendar(CalendarSelectionPurpose.Schedule, SelectedDate);
    [RelayCommand] private void ShowAssistant() => SetSection("plan");
#pragma warning disable MVVMTK0034
    [RelayCommand]
    private void ShowSettings()
    {
        if (IsSettings) { CloseSettings(); return; }
        SetProperty(ref _isCalendarPopupOpen, false, nameof(IsCalendarPopupOpen));
        CloseChatQuietly();
        SetProperty(ref _isAnalyticsOpen, false, nameof(IsAnalyticsOpen));
        IsSettings = true;
    }
    [RelayCommand] private void CloseSettings() => IsSettings = false;

    [RelayCommand]
    private void SelectSettingsSection(string? section)
        => SettingsSection = section?.ToLowerInvariant() switch
        {
            "appearance" => "appearance",
            // "assistant" and "models" were the two halves of what is now one section; both
            // names still resolve so older shortcuts and QA arguments keep working.
            "ai" or "assistant" or "models" => "ai",
            "data" or "sync" or "cloud" => "data",
            "about" => "about",
            _ => "general"
        };
    [RelayCommand] private void ToggleAssistantAdvanced() => IsAssistantAdvancedOpen = !IsAssistantAdvancedOpen;
    [RelayCommand]
    private void ToggleCalendar()
    {
        if (IsCalendarPopupOpen) CloseCalendar();
        else OpenCalendar(CalendarSelectionPurpose.Schedule, SelectedDate);
    }
    [RelayCommand] private void ToggleSchedule() => IsScheduleExpanded = !IsScheduleExpanded;
    [RelayCommand] private void ToggleTasks() => IsTasksExpanded = !IsTasksExpanded;
    [RelayCommand]
    private void ToggleChat()
    {
        if (IsChatOpen)
        {
            IsChatOpen = false;
            return;
        }

        SetProperty(ref _isCalendarPopupOpen, false, nameof(IsCalendarPopupOpen));
        SetProperty(ref _isSettings, false, nameof(IsSettings));
        SetProperty(ref _isAnalyticsOpen, false, nameof(IsAnalyticsOpen));
        CloseDatePicker();
        CloseTimePicker();
        IsChatOpen = true;
        _ = RefreshSuggestionsAsync();
    }
    /// <summary>
    /// Closes the assistant without running the property's own hook. The three panels that
    /// switch it off do so as one half of taking its place, and have already decided what
    /// becomes of the standing analytics charts; the hook would decide it again, from a state
    /// that is only half applied. The derived shapes are still announced, because everything
    /// that watches the assistant watches those rather than the flag underneath them.
    /// </summary>
    private void CloseChatQuietly()
    {
        SetProperty(ref _isChatOpen, false, nameof(IsChatOpen));
        OnPropertyChanged(nameof(IsChatSheetOpen));
        OnPropertyChanged(nameof(IsChatDocked));
        OnPropertyChanged(nameof(IsShellFlyoutOpen));
        OnPropertyChanged(nameof(HasBlockingSurface));
        RefreshChatSheetPresence();
    }
#pragma warning restore MVVMTK0034
    [RelayCommand] private void CloseChat() => IsChatOpen = false;
    /// <summary>
    /// Moves the conversation between the sheet over the planner and the panel beside it. Which
    /// one it was left in is what the next opening gets.
    /// </summary>
    [RelayCommand] private void ToggleChatExpanded() => IsChatExpanded = !IsChatExpanded;
    [RelayCommand]
    private void ClearAttachment()
    {
        AttachedFileName = string.Empty;
        AttachedFilePath = string.Empty;
        AttachedFileMeta = string.Empty;
        AttachmentPreview = null;
        IsAttachmentImage = false;
        AttachmentWarning = string.Empty;
    }
    [RelayCommand] private void CloseDatePicker() { IsEventDatePickerOpen = false; IsTaskDatePickerOpen = false; }
    [RelayCommand] private void CloseTimePicker() => IsTimePickerOpen = false;
    [RelayCommand] private void OpenEventStartTimePicker() => OpenTimePicker(TimeSelectionPurpose.EventStart, EventTime);
    [RelayCommand] private void OpenEventEndTimePicker() => OpenTimePicker(TimeSelectionPurpose.EventEnd, EventEndTime);
    [RelayCommand] private void OpenTaskBlockStartTimePicker() => OpenTimePicker(TimeSelectionPurpose.TaskBlockStart, TaskBlockStartTime);
    [RelayCommand] private void OpenTaskBlockEndTimePicker() => OpenTimePicker(TimeSelectionPurpose.TaskBlockEnd, TaskBlockEndTime);
    [RelayCommand] private void OpenDayShiftTimePicker() => OpenTimePicker(TimeSelectionPurpose.DayShift, TimeSpan.FromMinutes((double)(DayShiftHours * 60 + DayShiftMinutes)));
    [RelayCommand] private void OpenTaskDurationTimePicker()
    {
        var minutes = int.TryParse(EditDurationText, out var parsed) ? Math.Max(1, parsed) : 30;
        OpenTimePicker(TimeSelectionPurpose.TaskDuration, TimeSpan.FromMinutes(minutes));
    }
    [RelayCommand]
    private Task ApplyTimePickerAsync()
    {
        switch (_timeSelectionPurpose)
        {
            case TimeSelectionPurpose.EventStart: EventTime = TimePickerValue; break;
            case TimeSelectionPurpose.EventEnd: EventEndTime = TimePickerValue; break;
            case TimeSelectionPurpose.TaskBlockStart: TaskBlockStartTime = TimePickerValue; break;
            case TimeSelectionPurpose.TaskBlockEnd: TaskBlockEndTime = TimePickerValue; break;
            case TimeSelectionPurpose.DayShift:
                var shift = TimePickerValue ?? TimeSpan.Zero;
                DayShiftHours = (decimal)Math.Floor(shift.TotalHours);
                DayShiftMinutes = shift.Minutes;
                OnPropertyChanged(nameof(DayShiftAmountLabel));
                break;
            case TimeSelectionPurpose.TaskDuration:
                EditDurationText = Math.Max(1, (int)Math.Round((TimePickerValue ?? TimeSpan.Zero).TotalMinutes))
                    .ToString(localization.CurrentCulture);
                break;
            case TimeSelectionPurpose.MoveAmount:
                var amount = TimePickerValue ?? TimeSpan.Zero;
                MoveShiftHours = (decimal)Math.Floor(amount.TotalHours);
                MoveShiftMinutes = amount.Minutes;
                RefreshMovePreview();
                break;
            case TimeSelectionPurpose.ProjectDraftDuration or TimeSelectionPurpose.ProjectStepDuration
                or TimeSelectionPurpose.SittingDraftDuration or TimeSelectionPurpose.SittingStepDuration
                or TimeSelectionPurpose.FormDraftDuration:
                IsTimePickerOpen = false;
                return ApplyProjectDurationAsync(TimePickerValue);
        }
        IsTimePickerOpen = false;
        return Task.CompletedTask;
    }

    [RelayCommand] private void IncrementTimePickerHour() => AdjustTimePicker(60);
    [RelayCommand] private void DecrementTimePickerHour() => AdjustTimePicker(-60);
    [RelayCommand] private void IncrementTimePickerMinute() => AdjustTimePicker(1);
    [RelayCommand] private void DecrementTimePickerMinute() => AdjustTimePicker(-1);
    [RelayCommand]
    private void NudgeTimePicker(string? minutes)
    {
        if (int.TryParse(minutes, out var value) && value != 0) AdjustTimePicker(value);
    }
    [RelayCommand] private void ToggleTimePickerPeriod() => AdjustTimePicker(12 * 60);

    /// <summary>
    /// Accepts a file only if the assistant could actually send it, and says why not on the
    /// spot. Validating here rather than at request time is the difference between "that format
    /// is not supported" next to the chip and a failed answer with the attachment already gone.
    /// </summary>
    public void SetAttachedFile(string fileName, string filePath)
    {
        var kind = AttachmentSupport.Classify(filePath);
        if (kind == AttachmentKind.Unsupported)
        {
            ClearAttachment();
            Status = localization.Text("AttachUnsupported");
            AttachmentWarning = localization.Text("AttachUnsupported");
            return;
        }

        long length;
        try { length = new FileInfo(filePath).Length; }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Reading an attachment");
            ClearAttachment();
            AttachmentWarning = exception.Message;
            return;
        }

        if (length > AttachmentSupport.MaximumBytes)
        {
            ClearAttachment();
            AttachmentWarning = localization.Format("AttachTooLarge",
                AttachmentSupport.MaximumBytes / (1024 * 1024));
            return;
        }

        AttachedFileName = fileName;
        AttachedFilePath = filePath;
        IsAttachmentImage = kind == AttachmentKind.Image;
        AttachedFileMeta = FormatBytes(length);
        AttachmentWarning = IsAttachmentImage && !CanSendImages
            ? localization.Text("AttachImageNotSupported")
            : string.Empty;
        AttachmentPreview = IsAttachmentImage ? LoadPreview(filePath) : null;
    }

    /// <summary>Whether the current engine can accept an image at all.</summary>
    public bool CanSendImages => AssistantProvider switch
    {
        nameof(Domain.AssistantProvider.Mcp) => false,
        nameof(Domain.AssistantProvider.Local) => LocalModelVisionEnabled,
        _ => true
    };

    /// <summary>
    /// A small thumbnail decoded at reduced resolution. The composer is 40 pixels tall, so
    /// decoding a full phone photo into memory just to shrink it would be wasteful.
    /// </summary>
    private Avalonia.Media.Imaging.Bitmap? LoadPreview(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 96);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Building an attachment preview");
            return null;
        }
    }

    /// <summary>
    /// The four panels that stand beside the shell, and nothing else.
    ///
    /// Separate from <see cref="CloseShellFlyouts"/> because the shell is not the only thing
    /// these surfaces can be standing over: the quick widget asks its own question through the
    /// duration chooser, and the widget outlives the shell by design. Closing the panels when
    /// the shell goes must not take that chooser with it.
    /// </summary>
    public void CloseShellPanels()
    {
        IsCalendarPopupOpen = false;
        IsSettings = false;
        IsAnalyticsOpen = false;
        IsChatOpen = false;
    }

    public void CloseShellFlyouts()
    {
        CloseShellPanels();
        CloseTaskBlockEditor();
        // Ticks are a state the user is holding in their head as much as on the screen. A panel
        // that has gone away is not holding anything, so the selection ends with it rather than
        // waiting invisibly for the next "delete selected".
        ExitSelectionMode();
        CloseDatePicker();
        CloseTimePicker();
    }

    private void OpenTimePicker(TimeSelectionPurpose purpose, TimeSpan? value)
    {
        CloseDatePicker();
        _timeSelectionPurpose = purpose;
        var startTime = purpose is TimeSelectionPurpose.EventStart or TimeSelectionPurpose.TaskBlockStart;
        IsEditingStartTime = startTime;
        OnPropertyChanged(nameof(TimePickerTitle));
        OnPropertyChanged(nameof(IsTimePickerPeriodVisible));
        TimePickerValue = value ?? (startTime ? TimeSpan.FromHours(12) : TimeSpan.FromHours(13));
        IsTimePickerOpen = true;
    }

    private void AdjustTimePicker(int minutes)
    {
        const int minutesPerDay = 24 * 60;
        var current = (int)Math.Round((TimePickerValue ?? TimeSpan.Zero).TotalMinutes);
        if (_timeSelectionPurpose == TimeSelectionPurpose.MoveAmount)
        {
            TimePickerValue = TimeSpan.FromMinutes(Math.Clamp(current + minutes, 0, minutesPerDay - 1));
            return;
        }
        if (IsDurationPicker)
        {
            // A task may be days of work; one step of a project is a day of it at most.
            var maximum = _timeSelectionPurpose == TimeSelectionPurpose.TaskDuration ? 7 * minutesPerDay : minutesPerDay;
            TimePickerValue = TimeSpan.FromMinutes(Math.Clamp(current + minutes, 0, maximum));
            return;
        }
        var adjusted = (current + minutes) % minutesPerDay;
        if (adjusted < 0) adjusted += minutesPerDay;
        TimePickerValue = TimeSpan.FromMinutes(adjusted);
    }

    [RelayCommand]
    private async Task SubmitComposerAsync()
    {
        var input = ComposerInput.Trim();
        if (input.Length == 0 || IsBusy) return;
        ComposerInput = string.Empty;
        var attachment = AttachedFileName;
        var attachmentPath = AttachedFilePath;
        ClearAttachment();
        _lastAttachment = string.IsNullOrWhiteSpace(attachmentPath) ? null : (attachment, attachmentPath);
        _pendingAssistantAttachment = string.IsNullOrWhiteSpace(attachmentPath)
            ? null
            : new AssistantAttachment(attachmentPath, attachment);
        AssistantInput = input;
        await AskAssistantAsync();
    }

    [RelayCommand]
    private async Task SendQuickPromptAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || IsBusy) return;
        ComposerInput = prompt;
        await SubmitComposerAsync();
    }

    [RelayCommand]
    private void CloseCalendar() => IsCalendarPopupOpen = false;

#pragma warning disable MVVMTK0034
    private void OpenCalendar(CalendarSelectionPurpose purpose, DateOnly date)
    {
        _calendarSelectionPurpose = purpose;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        // Noted before the panels it replaces are switched off, because afterwards there is no
        // way left to tell a switch from an opening onto nothing.
        var takesAnotherPanelsPlace = IsSettings || IsAnalyticsOpen || IsChatDocked;
        SetProperty(ref _isSettings, false, nameof(IsSettings));
        CloseChatQuietly();
        SetProperty(ref _isAnalyticsOpen, false, nameof(IsAnalyticsOpen));
        IsCalendarPopupOpen = true;
        // The reads start at once; the two lists are rebuilt once the panel has finished
        // arriving — which, over another panel, is only after that one has finished leaving.
        var settled = takesAnotherPanelsPlace ? SwapSettled() : RevealSettled();
        Dispatcher.UIThread.Post(() => _ = FillCalendarSurfaceAsync(settled));
    }

    /// <summary>
    /// Fills the two halves of the calendar panel. Both reads go out together and each half is
    /// rebuilt as its own read comes back, so the month grid and the week board land in the same
    /// breath rather than one waiting on the other's trip to the database.
    /// </summary>
    private async Task FillCalendarSurfaceAsync(Task settled)
        => await Task.WhenAll(RefreshCalendarAsync(settled), RefreshSchedulerAsync(settled));
#pragma warning restore MVVMTK0034

    [RelayCommand]
    private async Task OpenEventDateCalendarAsync()
    {
        CloseTimePicker();
        _calendarSelectionPurpose = CalendarSelectionPurpose.Event;
        var date = EventDate is { } value ? DateOnly.FromDateTime(value.LocalDateTime) : SelectedDate;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        IsTaskDatePickerOpen = false;
        IsEventDatePickerOpen = !IsEventDatePickerOpen;
        if (IsEventDatePickerOpen) await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task OpenTaskDeadlineCalendarAsync()
    {
        CloseTimePicker();
        _calendarSelectionPurpose = CalendarSelectionPurpose.TaskDeadline;
        var date = EditDeadline is { } value ? DateOnly.FromDateTime(value.LocalDateTime) : SelectedDate;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        IsEventDatePickerOpen = false;
        IsTaskDatePickerOpen = !IsTaskDatePickerOpen;
        if (IsTaskDatePickerOpen) await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task OpenTaskEarliestCalendarAsync()
    {
        CloseTimePicker();
        _calendarSelectionPurpose = CalendarSelectionPurpose.TaskEarliestStart;
        var date = EditEarliestStart is { } value ? DateOnly.FromDateTime(value.LocalDateTime) : SelectedDate;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        IsEventDatePickerOpen = false;
        IsTaskDatePickerOpen = !IsTaskDatePickerOpen;
        if (IsTaskDatePickerOpen) await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task OpenTaskBlockDateCalendarAsync()
    {
        if (_editedTaskBlock is null) return;
        CloseTimePicker();
        _calendarSelectionPurpose = CalendarSelectionPurpose.TaskBlock;
        var date = TaskBlockDate is { } value ? DateOnly.FromDateTime(value.LocalDateTime) : SelectedDate;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        IsEventDatePickerOpen = false;
        IsTaskDatePickerOpen = !IsTaskDatePickerOpen;
        if (IsTaskDatePickerOpen) await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task PreviousCalendarMonthAsync()
    {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task NextCalendarMonthAsync()
    {
        _calendarMonth = _calendarMonth.AddMonths(1);
        await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task CalendarTodayAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        _calendarMonth = new DateOnly(today.Year, today.Month, 1);
        await SelectCalendarDayAsync(today);
    }

    [RelayCommand] private Task PreviousWeekAsync() => ShiftSelectedWeekAsync(-1);
    [RelayCommand] private Task NextWeekAsync() => ShiftSelectedWeekAsync(1);

    private async Task ShiftSelectedWeekAsync(int weeks)
    {
        // Enter an adjacent period through the nearest edge: Sunday when going back and Monday
        // when going forward. Keeping the selected weekday made the transition feel arbitrary;
        // always choosing Monday made backward navigation skip straight past the nearer Sunday.
        var date = CalendarWeek.NavigationTarget(SelectedDate, weeks);
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        await SelectDateSafelyAsync(date);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }

    private void SetSection(string section, bool persist = true)
    {
        if (section == "home" || section == "calendar") section = "plan";
        if (section is "assistant" or "tasks") section = "plan";
        if (section is not ("plan" or "tasks" or "assistant" or "settings")) section = "plan";
        IsHome = section == "plan"; IsTasks = section == "tasks"; IsCalendar = false;
        IsAssistant = section == "assistant"; IsSettings = section == "settings";
        OnPropertyChanged(nameof(ShowQuickCapture));
        if (persist && section != "settings" && _settings is not null)
        {
            _settings.LastSection = section;
            ScheduleSettingsSave();
        }
    }


    [RelayCommand]
    private void SelectLocalModel(LocalModelOptionViewModel? option)
    {
        if (option is null || IsLocalModelTransferring) return;
        var canonicalId = LocalModelCatalog.Find(option.Id).Id;

        if (IsLocalModelRunning || IsLocalModelStarting)
        {
            var runningId = localModels.Status.ModelId;
            if (!canonicalId.Equals(runningId, StringComparison.OrdinalIgnoreCase))
            {
                RequestSwitchModel(canonicalId, nameof(Domain.AssistantProvider.Local));
                return;
            }
        }

        LocalModelId = canonicalId;
        ApplyLocalModelStatus(localModels.GetStatus(Selection, CustomModelPath));
        ScheduleSettingsSave();
    }

    [RelayCommand]
    private async Task DownloadLocalModelAsync()
    {
        if (IsLocalModelTransferring) return;
        _localModelDownloadCancellation?.Cancel();
        _localModelDownloadCancellation?.Dispose();
        _localModelDownloadCancellation = new CancellationTokenSource();
        try
        {
            LocalModelId = LocalModelCatalog.Find(LocalModelId).Id;
            await localModels.DownloadAsync(Selection, IsVisionActive, LocalModelGpuEnabled,
                _localModelDownloadCancellation.Token);
            Status = localization.Text("LocalModelInstalled");
        }
        catch (OperationCanceledException) { Status = localization.Text("DownloadCancelled"); }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Installing local model");
            Status = exception.Message;
        }
        finally
        {
            _localModelDownloadCancellation?.Dispose();
            _localModelDownloadCancellation = null;
            ApplyLocalModelStatus(localModels.GetStatus(Selection, CustomModelPath));
            ScheduleSettingsSave();
        }
    }

    [RelayCommand] private void CancelLocalModelDownload() => _localModelDownloadCancellation?.Cancel();

    /// <summary>
    /// Puts the request parameters of whichever engine is answering back where they start. The
    /// other engine's set is left alone: resetting the API's numbers must not undo what was chosen
    /// for the local model, nor the other way round.
    /// </summary>
    [RelayCommand]
    private void ResetAssistantParameters()
    {
        ActiveParameters.Reset();
        AssistantExtraInstructions = string.Empty;
        if (IsLocalProviderActive && !LocalModelAutoTune)
        {
            LocalModelContextText = "8192";
            LocalModelThreadsText = "0";
        }
    }

    /// <summary>Raised so the settings window can open a file picker on the view's behalf.</summary>
    public event EventHandler<bool>? CustomModelPickRequested;
    public event EventHandler? CustomFontPickRequested;

    [RelayCommand] private void PickCustomModel() => CustomModelPickRequested?.Invoke(this, false);
    [RelayCommand] private void PickCustomProjector() => CustomModelPickRequested?.Invoke(this, true);
    [RelayCommand] private void PickCustomFont() => CustomFontPickRequested?.Invoke(this, EventArgs.Empty);

    public string FontDialogTitle => localization.Text("FontPickerTitle");

    public void ImportCustomFont(string path)
    {
        try
        {
            var font = fontCatalog.Import(path);
            BuildFontChoices(font.Value);
            AppFontFamily = font.Value;
            SelectedFontOption = FontOptions.First(option => option.Value == font.Value);
            PreviewCurrentTheme();
            ScheduleSettingsSave();
            Status = localization.Format("FontAdded", font.DisplayName);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Importing a custom font");
            Status = localization.Text("FontInvalid");
        }
    }

    [RelayCommand]
    private void ClearCustomModel()
    {
        CustomModelPath = string.Empty;
        CustomProjectorPath = string.Empty;
        if (IsCustomModelSelected) LocalModelId = LocalModelCatalog.DefaultModelId;
    }

    /// <summary>Called by the view once the user chose a file.</summary>
    public void SetCustomModelFile(string path, bool isProjector)
    {
        if (isProjector) CustomProjectorPath = path;
        else
        {
            CustomModelPath = path;
            LocalModelId = LocalModelCatalog.CustomModelId;
        }
        RefreshChatModelQuickOptions();
    }

    [RelayCommand]
    private async Task StartLocalModelAsync()
    {
        if (!CanStartLocalModel) return;
        try
        {
            await localModels.EnsureReadyAsync(Selection, RuntimeOptions);
            Status = localization.Text("LocalModelRunning");
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Starting local model");
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private async Task StopLocalModelAsync()
    {
        try
        {
            await localModels.StopAsync();
            Status = localization.Text("LocalModelStopped");
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Stopping local model");
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleLocalModelExecutionAsync()
    {
        if (IsLocalModelRunning || IsLocalModelStarting)
        {
            await StopLocalModelAsync();
        }
        else if (CanStartLocalModel)
        {
            await StartLocalModelAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteLocalModelAsync()
    {
        if (!CanDeleteLocalModel) return;
        try
        {
            await localModels.DeleteAsync(Selection);
            Status = localization.Text("LocalModelDeleted");
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Deleting local model");
            Status = exception.Message;
        }
        finally
        {
            ApplyLocalModelStatus(localModels.GetStatus(Selection, CustomModelPath));
            ScheduleSettingsSave();
        }
    }

    private async Task ToggleTaskAsync(CompactTaskViewModel item)
    {
        // A task broken into steps is done when its steps are, and its tick says so for all of them.
        if (await ToggleTaskProjectAsync(item.Task))
        {
            await ReloadAsync(preserveStatus: true);
            return;
        }

        var done = item.Task.Status != DomainTaskStatus.Done;
        item.Task.Status = done ? DomainTaskStatus.Done : DomainTaskStatus.Ready;
        item.Task.RemainingDurationMin = done ? 0 : Math.Max(5, item.Task.EstimatedDurationMin);
        await taskRepository.UpdateAsync(item.Task);
        Status = localization.Text(done ? "StatusReady" : "TaskRestored");
        await ReloadAsync(preserveStatus: true);
    }

    private Task BeginTaskEditAsync(CompactTaskViewModel item)
        => BeginTaskEditAsync(item.Task);

    private Task BeginTaskEditAsync(TaskItem task)
    {
        IsEventComposerVisible = false;
        _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        ResetEventConfirmation();
        _editedTask = task;
        OnPropertyChanged(nameof(IsEditingTask));
        PrepareTaskFormForProjects(task);
        TaskActionText = localization.Text("EditTask");
        EditTitle = task.Title; EditNotes = task.Notes ?? string.Empty;
        EditDuration = task.EstimatedDurationMin; EditPriority = task.Priority;
        EditDurationText = task.EstimatedDurationMin.ToString(localization.CurrentCulture);
        EditPriorityText = task.Priority.ToString(localization.CurrentCulture);
        EditDeadline = task.DeadlineUtc?.ToLocalTime();
        EditEarliestStart = task.EarliestStartUtc?.ToLocalTime();
        EditSplittable = task.Splittable;
        EditMinBlockText = task.MinBlockMin.ToString(localization.CurrentCulture);
        SelectedTaskPriorityOption = TaskPriorityOptions.OrderBy(option => Math.Abs(int.Parse(option.Value) - task.Priority)).FirstOrDefault();
        SelectedTaskPreferredTimeOption = TaskPreferredTimeOptions.FirstOrDefault(option => option.Value == task.PreferredTimeOfDay.ToString()) ?? TaskPreferredTimeOptions[0];
        SelectedTaskMaxSplitsOption = TaskMaxSplitsOptions.FirstOrDefault(option => option.Value == task.MaxSplits.ToString()) ?? TaskMaxSplitsOptions[1];
        IsTaskAdvancedVisible = task.EarliestStartUtc is not null || task.Splittable || task.PreferredTimeOfDay != PreferredTimeOfDay.Any || !string.IsNullOrWhiteSpace(task.Notes);
        RefreshDateLabels();
        IsTaskEditorVisible = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleTaskComposer()
    {
        if (IsTaskEditorVisible)
        {
            CancelTaskEdit();
            return;
        }

        CloseShellFlyouts();
        IsEventComposerVisible = false;
        _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        ResetEventConfirmation();
        _editedTask = null;
        OnPropertyChanged(nameof(IsEditingTask));
        PrepareTaskFormForProjects(null);
        TaskActionText = localization.Text("AddTask");
        EditTitle = string.Empty;
        EditNotes = string.Empty;
        EditDuration = 30;
        EditPriority = 50;
        EditDurationText = "30";
        EditPriorityText = "50";
        EditDeadline = null;
        EditEarliestStart = null;
        EditSplittable = false;
        EditMinBlockText = "30";
        SelectedTaskPriorityOption = TaskPriorityOptions.FirstOrDefault(option => option.Value == "50");
        SelectedTaskPreferredTimeOption = TaskPreferredTimeOptions.FirstOrDefault();
        SelectedTaskMaxSplitsOption = TaskMaxSplitsOptions.FirstOrDefault(option => option.Value == "3");
        IsTaskAdvancedVisible = false;
        RefreshDateLabels();
        IsTasksExpanded = true;
        IsTaskEditorVisible = true;
    }

    [RelayCommand] private Task SaveTaskEditAsync() => RunBusyAsync(SaveTaskEditCoreAsync);

    private async Task SaveTaskEditCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            Status = localization.Text("InvalidInput");
            return;
        }
        var priorityText = SelectedTaskPriorityOption?.Value ?? EditPriorityText;
        var maxSplitsText = SelectedTaskMaxSplitsOption?.Value ?? "3";
        if (!int.TryParse(EditDurationText, out var duration) || !int.TryParse(priorityText, out var priority) ||
            !int.TryParse(maxSplitsText, out var maxSplits) || duration <= 0)
        {
            Status = localization.Text("InvalidInput");
            return;
        }
        var normalizedDuration = NormalizeMinutes(duration);
        // A task written with steps is as long as they are.
        AddTaskDraftStep();
        if (TaskDraftSteps.Count > 0) normalizedDuration = NormalizeMinutes(TaskDraftSteps.Sum(step => step.Minutes));
        var minBlock = 30;
        if (EditSplittable && (!int.TryParse(EditMinBlockText, out minBlock) || minBlock <= 0))
        {
            Status = localization.Text("InvalidInput");
            return;
        }
        minBlock = Math.Min(normalizedDuration, NormalizeMinutes(minBlock));
        var preferredTime = Parse(SelectedTaskPreferredTimeOption?.Value ?? nameof(PreferredTimeOfDay.Any), PreferredTimeOfDay.Any);
        if (_editedTask is null)
        {
            var created = new TaskItem
            {
                Title = EditTitle.Trim(),
                Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim(),
                Status = DomainTaskStatus.Ready,
                EstimatedDurationMin = normalizedDuration,
                RemainingDurationMin = normalizedDuration,
                Priority = Math.Clamp(priority, 0, 100),
                DeadlineUtc = EditDeadline?.ToUniversalTime(),
                EarliestStartUtc = EditEarliestStart?.ToUniversalTime(),
                Splittable = EditSplittable,
                MinBlockMin = minBlock,
                MaxSplits = EditSplittable ? Math.Clamp(maxSplits, 2, 5) : 1,
                PreferredTimeOfDay = preferredTime
            };
            await taskRepository.AddAsync(created);
            await AttachTaskDraftStepsAsync(created);
            Status = localization.Text("TaskAdded");
        }
        else
        {
            var old = _editedTask.EstimatedDurationMin;
            _editedTask.Title = EditTitle.Trim();
            _editedTask.Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim();
            // A project's length is its steps'; the form does not ask for it and does not write it.
            if (!_editedTaskIsProject)
            {
                _editedTask.EstimatedDurationMin = normalizedDuration;
                if (_editedTask.RemainingDurationMin == old) _editedTask.RemainingDurationMin = normalizedDuration;
            }
            _editedTask.Priority = Math.Clamp(priority, 0, 100);
            _editedTask.DeadlineUtc = EditDeadline?.ToUniversalTime();
            _editedTask.EarliestStartUtc = EditEarliestStart?.ToUniversalTime();
            _editedTask.Splittable = EditSplittable;
            _editedTask.MinBlockMin = minBlock;
            _editedTask.MaxSplits = EditSplittable ? Math.Clamp(maxSplits, 2, 5) : 1;
            _editedTask.PreferredTimeOfDay = preferredTime;
            await taskRepository.UpdateAsync(_editedTask);
            await SaveTaskProjectCategoryAsync(_editedTask.Id);
            await AttachTaskDraftStepsAsync(_editedTask);
            Status = localization.Text("TaskUpdated");
        }
        IsTaskEditorVisible = false;
        _editedTask = null;
        OnPropertyChanged(nameof(IsEditingTask));
        await ReloadAsync(preserveStatus: true);
    }

    [RelayCommand]
    private void CancelTaskEdit()
    {
        IsTaskDatePickerOpen = false;
        IsTaskEditorVisible = false;
        _editedTask = null;
        OnPropertyChanged(nameof(IsEditingTask));
    }
    [RelayCommand] private void ClearTaskDeadline() { IsTaskDatePickerOpen = false; EditDeadline = null; RefreshDateLabels(); }
    [RelayCommand] private void ClearTaskEarliestStart() { IsTaskDatePickerOpen = false; EditEarliestStart = null; RefreshDateLabels(); }
    [RelayCommand] private void ToggleTaskAdvanced() => IsTaskAdvancedVisible = !IsTaskAdvancedVisible;

    private async Task DeleteTaskAsync(CompactTaskViewModel item)
    {
        await DeleteTaskProjectAsync(item.Task.Id);
        await taskRepository.DeleteAsync(item.Task.Id); Status = localization.Text("TaskDeleted");
        await ReloadAsync(preserveStatus: true);
    }

    private Task OpenTaskBlockAsync(TimeBlock block)
    {
        CancelTaskEdit();
        CloseDatePicker();
        CloseTimePicker();
        ResetEventConfirmation();
        IsEventComposerVisible = false;
        _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        _editedTaskBlock = block;
        _taskBlockOriginalStartUtc = block.StartUtc;
        _taskBlockOriginalEndUtc = block.EndUtc;
        var localStart = block.StartUtc.ToLocalTime();
        var localEnd = block.EndUtc.ToLocalTime();
        TaskBlockTitle = block.Task?.Title ?? block.TitleOverride ?? localization.Text("Focus");
        TaskBlockDate = localStart;
        TaskBlockStartTime = localStart.TimeOfDay;
        TaskBlockEndTime = localEnd.TimeOfDay;
        TaskBlockIsLocked = block.IsLocked;
        TaskBlockCompletedMinutesText = block.DurationMinutes.ToString(localization.CurrentCulture);
        TaskBlockValidationMessage = string.Empty;
        ResetTaskBlockConfirmation();
        RefreshDateLabels();
        OnPropertyChanged(nameof(CanEditTaskBlock));
        OnPropertyChanged(nameof(CanCompleteTaskBlock));
        IsTaskBlockEditorVisible = true;
        return Task.CompletedTask;
    }

    public void ShowTaskBlock(TimeBlock block) => _ = OpenTaskBlockAsync(block);

    public void BeginTaskBlockGridChange(TimeBlock block, int dayIndex, int startMinuteOfDay, int endMinuteOfDay)
    {
        _ = OpenTaskBlockAsync(block);
        var date = StartOfWeek(SelectedDate).AddDays(Math.Clamp(dayIndex, 0, 6));
        startMinuteOfDay = ((startMinuteOfDay % 1440) + 1440) % 1440;
        endMinuteOfDay = ((endMinuteOfDay % 1440) + 1440) % 1440;
        if (endMinuteOfDay == startMinuteOfDay) endMinuteOfDay = (startMinuteOfDay + 30) % 1440;
        TaskBlockDate = ToLocalOffset(date);
        TaskBlockStartTime = TimeSpan.FromMinutes(startMinuteOfDay);
        TaskBlockEndTime = TimeSpan.FromMinutes(endMinuteOfDay);
        TaskBlockIsLocked = true;
        RefreshDateLabels();
        RequestTaskBlockAction(TaskBlockPendingAction.Save, "ConfirmTaskBlockChange");
    }

    [RelayCommand]
    private void CloseTaskBlockEditor()
    {
        IsTaskBlockEditorVisible = false;
        IsTaskBlockConfirmationVisible = false;
        TaskBlockValidationMessage = string.Empty;
        _editedTaskBlock = null;
        _pendingTaskBlockAction = TaskBlockPendingAction.None;
        if (_calendarSelectionPurpose == CalendarSelectionPurpose.TaskBlock) CloseDatePicker();
        if (_timeSelectionPurpose is TimeSelectionPurpose.TaskBlockStart or TimeSelectionPurpose.TaskBlockEnd) CloseTimePicker();
        OnPropertyChanged(nameof(CanEditTaskBlock));
        OnPropertyChanged(nameof(CanCompleteTaskBlock));
    }

    [RelayCommand] private void RequestTaskBlockSave() => RequestTaskBlockAction(TaskBlockPendingAction.Save, "ConfirmTaskBlockChange");
    [RelayCommand] private void RequestTaskBlockComplete() => RequestTaskBlockAction(TaskBlockPendingAction.Complete, "ConfirmTaskBlockComplete");
    [RelayCommand] private void RequestTaskBlockUnschedule() => RequestTaskBlockAction(TaskBlockPendingAction.Unschedule, "ConfirmTaskBlockUnschedule");
    [RelayCommand] private void RequestTaskBlockNotToday() => RequestTaskBlockAction(TaskBlockPendingAction.NotToday, "ConfirmTaskBlockNotToday");
    [RelayCommand] private void CancelTaskBlockConfirmation() => ResetTaskBlockConfirmation();
    [RelayCommand] private Task ConfirmTaskBlockActionAsync() => RunBusyAsync(ConfirmTaskBlockActionCoreAsync);

    private void RequestTaskBlockAction(TaskBlockPendingAction action, string confirmationKey)
    {
        if (_editedTaskBlock is null) return;
        if (action == TaskBlockPendingAction.Save && !ValidateTaskBlockRange(out _, out _)) return;
        if (action == TaskBlockPendingAction.Complete &&
            (!int.TryParse(TaskBlockCompletedMinutesText, out var minutes) || minutes <= 0 || minutes > _editedTaskBlock.DurationMinutes))
        {
            TaskBlockValidationMessage = localization.Text("TaskBlockMinutesInvalid");
            return;
        }
        TaskBlockValidationMessage = string.Empty;
        _pendingTaskBlockAction = action;
        TaskBlockConfirmationText = localization.Text(confirmationKey);
        IsTaskBlockConfirmationVisible = true;
    }

    private async Task ConfirmTaskBlockActionCoreAsync()
    {
        if (_editedTaskBlock is not { } block) return;
        switch (_pendingTaskBlockAction)
        {
            case TaskBlockPendingAction.Save:
                if (!ValidateTaskBlockRange(out var startUtc, out var endUtc)) return;
                var conflictingEvents = await calendarRepository.GetRangeAsync(startUtc, endUtc);
                var conflictingBlocks = await blockRepository.GetRangeAsync(startUtc, endUtc);
                if (conflictingEvents.Count > 0 || conflictingBlocks.Any(item => item.Id != block.Id))
                {
                    TaskBlockValidationMessage = localization.Text("TaskBlockConflict");
                    ResetTaskBlockConfirmation();
                    return;
                }
                var moved = startUtc != _taskBlockOriginalStartUtc || endUtc != _taskBlockOriginalEndUtc;
                block.PreviousStartUtc = moved ? _taskBlockOriginalStartUtc : block.PreviousStartUtc;
                block.StartUtc = startUtc;
                block.EndUtc = endUtc;
                block.IsLocked = moved || TaskBlockIsLocked;
                await blockRepository.UpdateAsync(block);
                Status = localization.Text(moved ? "TaskBlockMoved" : "TaskBlockUpdated");
                break;
            case TaskBlockPendingAction.Complete:
                await CompleteTaskBlockAsync(block);
                break;
            case TaskBlockPendingAction.Unschedule:
                block.State = BlockState.Cancelled;
                await blockRepository.UpdateAsync(block);
                if (block.TaskId is { } unscheduledTaskId)
                    await blockRepository.AddExclusionAsync(new TaskScheduleExclusion
                    {
                        TaskId = unscheduledTaskId,
                        StartUtc = _taskBlockOriginalStartUtc,
                        EndUtc = _taskBlockOriginalEndUtc,
                        Kind = TaskScheduleExclusionKind.ManualUnschedule
                    });
                if (block.Task is { } unscheduledTask)
                {
                    unscheduledTask.Status = DomainTaskStatus.Ready;
                    await taskRepository.UpdateAsync(unscheduledTask);
                }
                Status = localization.Text("TaskBlockUnscheduled");
                break;
            case TaskBlockPendingAction.NotToday:
                block.State = BlockState.Skipped;
                await blockRepository.UpdateAsync(block);
                if (block.TaskId is { } skippedTaskId)
                {
                    var localDate = DateOnly.FromDateTime(_taskBlockOriginalStartUtc.ToLocalTime().Date);
                    var (dayStart, dayEnd) = DayRange(localDate);
                    await blockRepository.AddExclusionAsync(new TaskScheduleExclusion
                    {
                        TaskId = skippedTaskId,
                        StartUtc = dayStart,
                        EndUtc = dayEnd,
                        Kind = TaskScheduleExclusionKind.NotToday
                    });
                }
                if (block.Task is { } skippedTask)
                {
                    skippedTask.Status = DomainTaskStatus.Ready;
                    await taskRepository.UpdateAsync(skippedTask);
                }
                Status = localization.Text("TaskBlockDeferred");
                break;
        }
        CloseTaskBlockEditor();
        await RefreshCalendarAsync();
    }

    private async Task CompleteTaskBlockAsync(TimeBlock block)
    {
        var plannedMinutes = block.DurationMinutes;
        if (!int.TryParse(TaskBlockCompletedMinutesText, out var actualMinutes) || actualMinutes <= 0 || actualMinutes > plannedMinutes)
        {
            TaskBlockValidationMessage = localization.Text("TaskBlockMinutesInvalid");
            return;
        }
        await blockRepository.CompleteTaskBlockAsync(block.Id, actualMinutes);
        Status = localization.Text(actualMinutes < plannedMinutes ? "TaskBlockPartiallyCompleted" : "BlockCompleted");
    }

    private bool ValidateTaskBlockRange(out DateTimeOffset startUtc, out DateTimeOffset endUtc)
    {
        startUtc = default;
        endUtc = default;
        if (TaskBlockDate is not { } date || TaskBlockStartTime is not { } startTime || TaskBlockEndTime is not { } endTime || startTime == endTime)
        {
            TaskBlockValidationMessage = localization.Text("EventTimeRangeRequired");
            return false;
        }
        var (localStart, localEnd) = EventTimeRange.ResolveLocal(DateOnly.FromDateTime(date.LocalDateTime), startTime, endTime);
        startUtc = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)).ToUniversalTime();
        endUtc = new DateTimeOffset(localEnd, TimeZoneInfo.Local.GetUtcOffset(localEnd)).ToUniversalTime();
        TaskBlockValidationMessage = string.Empty;
        return true;
    }

    [RelayCommand]
    private Task OpenTaskFromBlockAsync()
    {
        var task = _editedTaskBlock?.Task;
        if (task is null) return Task.CompletedTask;
        CloseTaskBlockEditor();
        IsCalendarPopupOpen = false;
        IsTasksExpanded = true;
        return BeginTaskEditAsync(task);
    }

    private void ResetTaskBlockConfirmation()
    {
        IsTaskBlockConfirmationVisible = false;
        TaskBlockConfirmationText = string.Empty;
        _pendingTaskBlockAction = TaskBlockPendingAction.None;
    }

    private async Task CompleteAgendaItemAsync(AgendaItemViewModel item)
    {
        if (item.Block is not { } block) return;
        await blockRepository.CompleteTaskBlockAsync(block.Id, block.DurationMinutes);
        Status = localization.Text("BlockCompleted"); await ReloadAsync(preserveStatus: true);
    }

    [RelayCommand]
    private void ToggleEventComposer()
    {
        if (IsEventComposerVisible)
        {
            CloseDatePicker();
            CloseTimePicker();
            ResetEventConfirmation();
            ResetEventDrafts(allowed: true);
            IsEventComposerVisible = false;
            _editedEvent = null;
            OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
            return;
        }
        CloseShellFlyouts();
        CancelTaskEdit();
        CloseDatePicker();
        ResetEventConfirmation();
        ResetEventDrafts(allowed: true);
        _editedEvent = null; EventTitle = string.Empty; EventValidationMessage = string.Empty;
        SelectEventPlanningOptions();
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        var startMinute = SelectedDate == DateOnly.FromDateTime(DateTime.Today)
            ? Math.Min(23 * 60 + 59, (int)Math.Ceiling(DateTime.Now.TimeOfDay.TotalMinutes))
            : 12 * 60;
        var endMinute = (startMinute + 60) % (24 * 60);
        _eventEndDayOffset = 0;
        EventTime = TimeSpan.FromMinutes(startMinute); EventEndTime = TimeSpan.FromMinutes(endMinute);
        EventTimeText = FormatClockTime(EventTime); EventEndTimeText = FormatClockTime(EventEndTime); EventActionText = localization.Text("AddEvent");
        var local = DateTime.SpecifyKind(SelectedDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        EventDate = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        RefreshDateLabels();
        IsScheduleExpanded = true;
        IsEventComposerVisible = true;
    }

    private void SelectEventPlanningOptions(CalendarEvent? calendarEvent = null)
    {
        var category = calendarEvent?.Category ?? EventCategory.General;
        var priority = calendarEvent?.Priority ?? EventPriority.Normal;
        var recurrence = calendarEvent?.Recurrence ?? EventRecurrence.None;
        SelectedEventCategoryOption = EventCategoryOptions.FirstOrDefault(option => option.Value == category.ToString()) ?? EventCategoryOptions.FirstOrDefault();
        SelectedEventPriorityOption = EventPriorityOptions.FirstOrDefault(option => option.Value == priority.ToString()) ?? EventPriorityOptions.FirstOrDefault();
        SelectedEventRecurrenceOption = EventRecurrenceOptions.FirstOrDefault(option => option.Value == recurrence.ToString()) ?? EventRecurrenceOptions.FirstOrDefault();
    }
    [RelayCommand]
    private void ToggleDayActions()
    {
        IsDayActionsOpen = !IsDayActionsOpen;
        if (!IsDayActionsOpen) return;
        // The extra answers are folded away again each time the sheet is opened, and they go
        // back to meaning nothing while they are: a day count left over from last time would
        // be applied by a Shift button that has no way of saying so. The day they would land
        // on starts as the one after the day being looked at.
        IsDayShiftMoreOpen = false;
        IsDayShiftByDate = false;
        DayShiftDays = 0;
        OnPropertyChanged(nameof(DayShiftDaysLabel));
        SetDayShiftTarget(SelectedDate.AddDays(1));
    }
    [RelayCommand] private void CloseDayActions() => IsDayActionsOpen = false;
    [RelayCommand] private void SetDayShiftEarlier() { IsDayShiftEarlier = true; IsDayShiftByDate = false; }
    [RelayCommand] private void SetDayShiftLater() { IsDayShiftEarlier = false; IsDayShiftByDate = false; }

    /// <summary>Opens or folds away the days-and-dates half of the shift controls.</summary>
    [RelayCommand] private void ToggleDayShiftMore() => IsDayShiftMoreOpen = !IsDayShiftMoreOpen;

    /// <summary>
    /// Adds to or takes from the whole days the shift carries. The direction is the one the
    /// section is already set to, so this is a count rather than a signed number.
    /// </summary>
    [RelayCommand]
    private void NudgeDayShiftDays(string? days)
    {
        if (!int.TryParse(days, out var value) || value == 0) return;
        DayShiftDays = Math.Clamp(DayShiftDays + value, 0, 365);
        // Naming an amount answers a different question than naming a day to land on.
        IsDayShiftByDate = false;
        OnPropertyChanged(nameof(DayShiftDaysLabel));
    }

    /// <summary>Picks the day the whole schedule should land on instead of an amount.</summary>
    [RelayCommand]
    private Task OpenDayShiftDateCalendarAsync()
        => OpenDateSelectionAsync(CalendarSelectionPurpose.DayShiftTarget, _dayShiftTargetDate);

    private void SetDayShiftTarget(DateOnly date)
    {
        _dayShiftTargetDate = date;
        DayShiftTargetDateLabel = date.ToString("ddd, d MMM", localization.CurrentCulture);
    }

    [RelayCommand]
    private async Task RequestShiftDayAsync()
    {
        _pendingDayShiftMoves = [];
        _pendingDayShift = TimeSpan.Zero;
        var day = SelectedDate.ToString("d MMMM", localization.CurrentCulture);

        if (IsDayShiftByDate)
        {
            var offsetDays = _dayShiftTargetDate.DayNumber - SelectedDate.DayNumber;
            if (offsetDays == 0)
            {
                Status = localization.Text("ShiftDayAmountRequired");
                return;
            }

            _pendingDayShiftMoves = ScheduleShift.ByDays(await DayEventsAsync(), offsetDays);
            ShowShiftDayConfirmation(localization.Format("ShiftDayDateConfirmation",
                day, _dayShiftTargetDate.ToString("d MMMM", localization.CurrentCulture)));
            return;
        }

        var wholeDays = (int)Math.Clamp(DayShiftDays, 0, 365);
        var totalMinutes = (int)(Math.Clamp(DayShiftHours, 0, 720) * 60 + Math.Clamp(DayShiftMinutes, 0, 59));
        if (wholeDays == 0 && totalMinutes <= 0)
        {
            Status = localization.Text("ShiftDayAmountRequired");
            return;
        }

        if (wholeDays > 0)
        {
            // Whole days are carried as local days rather than as blocks of twenty-four hours,
            // so an event at nine in the morning is still at nine on the other side of a clock
            // change. Hours and minutes named alongside them are real time and ride on top.
            var signedDays = IsDayShiftEarlier ? -wholeDays : wholeDays;
            var moves = ScheduleShift.ByDays(await DayEventsAsync(), signedDays);
            if (totalMinutes > 0)
            {
                var extra = TimeSpan.FromMinutes(IsDayShiftEarlier ? -totalMinutes : totalMinutes);
                moves = [.. moves.Select(move => move with
                {
                    ToStartUtc = move.ToStartUtc + extra,
                    ToEndUtc = move.ToEndUtc + extra
                })];
            }
            _pendingDayShiftMoves = moves;
        }
        else
        {
            _pendingDayShift = TimeSpan.FromMinutes(IsDayShiftEarlier ? -totalMinutes : totalMinutes);
        }

        ShowShiftDayConfirmation(localization.Format("ShiftDayConfirmation",
            day, FormatDayShiftAmount(wholeDays, totalMinutes)));
    }

    private async Task<IReadOnlyList<CalendarEvent>> DayEventsAsync()
    {
        var (start, end) = DayRange(SelectedDate);
        return [.. await calendarRepository.GetRangeAsync(start, end)];
    }

    private void ShowShiftDayConfirmation(string text)
    {
        IsDayActionsOpen = false;
        IsShiftDayConfirmationVisible = true;
        ShiftDayConfirmationText = text;
    }

    /// <summary>The shift written out: its direction, its whole days, and the clock left over.</summary>
    private string FormatDayShiftAmount(int days, int minutes)
    {
        var sign = IsDayShiftEarlier ? "−" : "+";
        var clock = $"{minutes / 60}:{minutes % 60:00}";
        if (days == 0) return $"{sign}{clock}";
        var wholeDays = localization.Format("MetricDays", days);
        return minutes == 0 ? $"{sign}{wholeDays}" : $"{sign}{wholeDays} {clock}";
    }

    [RelayCommand]
    private void CancelShiftDay()
    {
        IsShiftDayConfirmationVisible = false;
        _pendingDayShiftMoves = [];
    }
    [RelayCommand] private Task ConfirmShiftDayAsync() => RunBusyAsync(ShiftDayCoreAsync);
    [RelayCommand] private void RequestCopyDayTomorrow() => RequestCopyDay(1);
    [RelayCommand] private void RequestCopyDayNextWeek() => RequestCopyDay(7);
    [RelayCommand] private Task RequestCopyDayToDateAsync() => OpenDateSelectionAsync(CalendarSelectionPurpose.DayCopy, SelectedDate.AddDays(1));
    [RelayCommand] private void RequestClearDay()
    {
        _isClearDayPending = true;
        IsDayActionsOpen = false;
        IsCopyDayConfirmationVisible = true;
        CopyDayTargetText = localization.Format("ClearDayConfirmation", SelectedDate.ToString("d MMMM", localization.CurrentCulture));
    }
    [RelayCommand] private void CancelCopyDay() { IsCopyDayConfirmationVisible = false; _isClearDayPending = false; }
    [RelayCommand] private Task ConfirmCopyDayAsync() => RunBusyAsync(_isClearDayPending ? ClearDayCoreAsync : CopyDayCoreAsync);

    private void RequestCopyDay(int offsetDays)
    {
        _isClearDayPending = false;
        _copyDayOffsetDays = offsetDays;
        IsDayActionsOpen = false;
        IsCopyDayConfirmationVisible = true;
        var target = SelectedDate.AddDays(offsetDays);
        CopyDayTargetText = localization.Format("CopyDayConfirmation", target.ToString("d MMMM", localization.CurrentCulture));
    }

    private async Task OpenDateSelectionAsync(CalendarSelectionPurpose purpose, DateOnly date)
    {
        _calendarSelectionPurpose = purpose;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        IsTaskDatePickerOpen = false;
        IsEventDatePickerOpen = true;
        await RefreshCalendarAsync();
    }

    private async Task CopyDayCoreAsync()
    {
        var (sourceStart, sourceEnd) = DayRange(SelectedDate);
        var source = await calendarRepository.GetRangeAsync(sourceStart, sourceEnd);
        var targetDate = SelectedDate.AddDays(_copyDayOffsetDays);
        var (targetStart, targetEnd) = DayRange(targetDate);
        var existing = await calendarRepository.GetRangeAsync(targetStart, targetEnd);
        var candidates = source.Select(item => CopyEventToDay(item, _copyDayOffsetDays)).ToList();
        var copies = candidates
            .Where(copy => !existing.Any(target => target.Title == copy.Title && target.StartUtc == copy.StartUtc && target.EndUtc == copy.EndUtc))
            .ToList();
        await calendarRepository.AddRangeAsync(copies);
        for (var index = 0; index < candidates.Count; index++)
            if (copies.Contains(candidates[index])) await CopySittingAsync(source[index], candidates[index]);
        IsCopyDayConfirmationVisible = false;
        Status = localization.Format("CopyDayCompleted", copies.Count);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }

    private async Task ShiftDayCoreAsync()
    {
        int shiftedCount;
        if (_pendingDayShiftMoves.Count > 0)
        {
            var moves = _pendingDayShiftMoves.Where(move => !move.IsNoop).ToList();
            shiftedCount = moves.Count == 0 ? 0 : await calendarRepository.MoveEventsAsync(moves);
            _pendingDayShiftMoves = [];
        }
        else
        {
            var (start, end) = DayRange(SelectedDate);
            shiftedCount = await calendarRepository.ShiftRangeAsync(start, end, _pendingDayShift);
        }

        IsShiftDayConfirmationVisible = false;
        Status = localization.Format("ShiftDayCompleted", shiftedCount);
        await ReloadAsync(preserveStatus: true);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }
    private async Task ClearDayCoreAsync()
    {
        var (start, end) = DayRange(SelectedDate);
        var events = await calendarRepository.GetRangeAsync(start, end);
        foreach (var calendarEvent in events)
            await calendarRepository.DeleteScopeAsync(calendarEvent, EventSeriesScope.Occurrence);
        _isClearDayPending = false;
        IsCopyDayConfirmationVisible = false;
        Status = localization.Format("ClearDayCompleted", events.Count);
        await ReloadAsync(preserveStatus: true);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }

    private async Task RequestCopyEventAsync(AgendaItemViewModel item)
    {
        if (item.Event is null) return;
        _eventToCopy = item.Event;
        var sourceDate = DateOnly.FromDateTime(item.Event.StartUtc.ToLocalTime().Date);
        await OpenDateSelectionAsync(CalendarSelectionPurpose.EventCopy, sourceDate.AddDays(1));
    }

    [RelayCommand] private void CancelEventCopy() { IsEventCopyConfirmationVisible = false; _eventToCopy = null; }
    [RelayCommand] private Task ConfirmEventCopyAsync() => RunBusyAsync(EventCopyCoreAsync);

    private async Task EventCopyCoreAsync()
    {
        if (_eventToCopy is null) return;
        var sourceDate = DateOnly.FromDateTime(_eventToCopy.StartUtc.ToLocalTime().Date);
        var offset = _copyDayOffsetDays;
        var copy = CopyEventToDay(_eventToCopy, offset);
        await calendarRepository.AddAsync(copy);
        await CopySittingAsync(_eventToCopy, copy);
        IsEventCopyConfirmationVisible = false;
        _eventToCopy = null;
        Status = localization.Format("EventCopied", sourceDate.AddDays(offset).ToString("d MMMM", localization.CurrentCulture));
        await ReloadAsync(preserveStatus: true);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }

    private static CalendarEvent CopyEventToDay(CalendarEvent source, int offsetDays)
    {
        static DateTimeOffset Shift(DateTimeOffset value, int days)
        {
            var local = DateTime.SpecifyKind(value.ToLocalTime().LocalDateTime.AddDays(days), DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        }

        return new CalendarEvent
        {
            Title = source.Title,
            StartUtc = Shift(source.StartUtc, offsetDays),
            EndUtc = Shift(source.EndUtc, offsetDays),
            Source = "Local",
            Category = source.Category,
            Priority = source.Priority,
            Flexibility = source.Flexibility,
            Recurrence = EventRecurrence.None
        };
    }    [RelayCommand] private Task AddEventAsync() => RunBusyAsync(() => AddEventCoreAsync(EventSeriesScope.Occurrence));

    [RelayCommand]
    private Task RequestEventSaveAsync()
    {
        if (!ValidateEventInput(out _, out _)) return Task.CompletedTask;
        if (_editedEvent is null) return RunBusyAsync(() => AddEventCoreAsync(EventSeriesScope.Occurrence));
        IsEventSaveConfirmationVisible = true;
        return Task.CompletedTask;
    }

    [RelayCommand] private Task ConfirmEventSaveAsync() => RunBusyAsync(() => AddEventCoreAsync(Parse(SelectedEventEditScopeOption?.Value ?? nameof(EventSeriesScope.Occurrence), EventSeriesScope.Occurrence)));
    [RelayCommand] private void CancelEventConfirmation() => ResetEventConfirmation();

    private async Task AddEventCoreAsync(EventSeriesScope editScope)
    {
        if (!ValidateEventInput(out var eventStart, out var eventEnd)) return;
        var eventDate = EventDate.GetValueOrDefault();
        var eventDay = DateOnly.FromDateTime(eventDate.Date);
        DateTime localStart;
        DateTime localEnd;
        if (_eventEndDayOffset > 0)
        {
            localStart = DateTime.SpecifyKind(eventDay.ToDateTime(TimeOnly.MinValue).Add(eventStart), DateTimeKind.Unspecified);
            localEnd = DateTime.SpecifyKind(eventDay.AddDays(_eventEndDayOffset).ToDateTime(TimeOnly.MinValue).Add(eventEnd), DateTimeKind.Unspecified);
        }
        else
        {
            (localStart, localEnd) = EventTimeRange.ResolveLocal(eventDay, eventStart, eventEnd);
        }
        var start = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)).ToUniversalTime();
        var end = new DateTimeOffset(localEnd, TimeZoneInfo.Local.GetUtcOffset(localEnd)).ToUniversalTime();
        try
        {
            if (_editedEvent is null)
            {
                var calendarEvent = new CalendarEvent
                {
                    Title = EventTitle.Trim(),
                    StartUtc = start,
                    EndUtc = end,
                    Category = _eventCategoryDraft,
                    Priority = _eventPriorityDraft,
                    Flexibility = EventFlexibility.Fixed,
                    Recurrence = _eventRecurrenceDraft
                };
                var host = calendarEvent;
                if (calendarEvent.Recurrence == EventRecurrence.None)
                    await calendarRepository.AddAsync(calendarEvent);
                else
                {
                    var series = EventRecurrenceExpander.Expand(calendarEvent, start.AddYears(1));
                    await calendarRepository.AddRangeAsync(series);
                    // A repeating slot opened for a project step is tied to its first occurrence,
                    // and a checklist written with a repeating event belongs to the whole series.
                    host = series.OrderBy(item => item.StartUtc).First();
                }
                await LinkScheduledStepAsync(host.Id, eventDay);
                await AttachEventDraftStepsAsync(host);
                Status = localization.Text("EventAdded");
            }
            else
            {
                var updated = new CalendarEvent
                {
                    Id = _editedEvent.Id,
                    Title = EventTitle.Trim(),
                    StartUtc = start,
                    EndUtc = end,
                    ExternalId = _editedEvent.ExternalId,
                    Source = _editedEvent.Source,
                    Category = _eventCategoryDraft,
                    Priority = _eventPriorityDraft,
                    Flexibility = _editedEvent.Flexibility,
                    Recurrence = _eventRecurrenceDraft,
                    SeriesId = _editedEvent.SeriesId,
                    OccurrenceIndex = _editedEvent.OccurrenceIndex,
                    IsRecurrenceException = _editedEvent.SeriesId.HasValue && editScope == EventSeriesScope.Occurrence,
                    CreatedAtUtc = _editedEvent.CreatedAtUtc
                };
                if (updated.Recurrence != _editedEvent.Recurrence)
                {
                    IReadOnlyCollection<CalendarEvent> replacements;
                    if (updated.Recurrence == EventRecurrence.None)
                    {
                        updated.Id = Guid.NewGuid();
                        updated.SeriesId = null;
                        updated.OccurrenceIndex = 0;
                        updated.IsRecurrenceException = false;
                        replacements = [updated];
                    }
                    else
                    {
                        updated.Id = Guid.NewGuid();
                        updated.SeriesId = null;
                        updated.OccurrenceIndex = 0;
                        updated.IsRecurrenceException = false;
                        replacements = EventRecurrenceExpander.Expand(updated, updated.StartUtc.AddYears(1));
                    }
                    await calendarRepository.ReplaceScopeAsync(_editedEvent, editScope, replacements);
                    await RehostSittingAsync(_editedEvent, editScope, replacements);
                    await AttachEventDraftStepsAsync(replacements.OrderBy(item => item.StartUtc).First());
                }
                else
                {
                    if (_editedEvent.SeriesId.HasValue)
                        await calendarRepository.UpdateScopeAsync(_editedEvent, updated, editScope);
                    else
                        await calendarRepository.UpdateAsync(updated);
                    await AttachEventDraftStepsAsync(updated);
                }
                Status = localization.Text("EventUpdated");
            }
        }
        catch
        {
            EventValidationMessage = localization.Text("EventSaveFailed");
            throw;
        }
        EventTitle = string.Empty; IsEventComposerVisible = false; _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        ResetEventConfirmation();
        await ReloadAsync(preserveStatus: true);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }

    private bool ValidateEventInput(out TimeSpan eventStart, out TimeSpan eventEnd)
    {
        eventStart = default;
        eventEnd = default;
        if (string.IsNullOrWhiteSpace(EventTitle))
        {
            EventValidationMessage = localization.Text("EventTitleRequired");
            Status = EventValidationMessage;
            return false;
        }
        if (EventDate is null || EventTime is not { } start || EventEndTime is not { } end)
        {
            EventValidationMessage = localization.Text("EventDateTimeRequired");
            Status = EventValidationMessage;
            return false;
        }
        if (start == end && _eventEndDayOffset == 0)
        {
            EventValidationMessage = localization.Text("EventTimeRangeRequired");
            Status = EventValidationMessage;
            return false;
        }
        eventStart = start;
        eventEnd = end;
        EventValidationMessage = string.Empty;
        return true;
    }

    private Task BeginEventEditAsync(AgendaItemViewModel item)
    {
        if (item.Event is not { } calendarEvent) return Task.CompletedTask;
        return BeginEventEditAsync(calendarEvent);
    }

    private Task BeginEventEditAsync(CalendarEvent calendarEvent)
    {
        CancelTaskEdit();
        // Editing an event that already exists books nothing for a project step. Its steps, if
        // it has any, are written on its own row; the form only breaks up an event that has none.
        _schedulingStepId = null;
        ResetEventDrafts(allowed: SittingProjectFor(calendarEvent) is null && !IsStepSlot(calendarEvent.Id));
        _editedEvent = calendarEvent;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        ResetEventConfirmation();
        var localStart = calendarEvent.StartUtc.ToLocalTime();
        var localEnd = calendarEvent.EndUtc.ToLocalTime();
        SelectEventPlanningOptions(calendarEvent);
        _eventEndDayOffset = Math.Max(0, (localEnd.Date - localStart.Date).Days);
        EventTitle = calendarEvent.Title; EventDate = localStart; EventTime = localStart.TimeOfDay;
        EventEndTime = localEnd.TimeOfDay;
        EventTimeText = ClockText.Format(localStart.TimeOfDay, Use24HourClock, localization.CurrentCulture);
        EventEndTimeText = ClockText.Format(localEnd.TimeOfDay, Use24HourClock, localization.CurrentCulture);
        RefreshDateLabels();
        EventActionText = localization.Text("EditEvent"); IsScheduleExpanded = true; IsEventComposerVisible = true;
        return Task.CompletedTask;
    }

    private async Task DeleteEventAsync(AgendaItemViewModel item, EventSeriesScope scope)
    {
        if (item.Event is null) return;
        await DeleteEventCoreAsync(item.Event, scope);
    }

    private async Task DeleteEventCoreAsync(CalendarEvent calendarEvent, EventSeriesScope scope = EventSeriesScope.Occurrence)
    {
        await calendarRepository.DeleteScopeAsync(calendarEvent, scope);
        Status = localization.Text("EventDeleted");
        IsEventComposerVisible = false;
        CloseDatePicker();
        ResetEventConfirmation();
        _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        // Refresh first: replanning is secondary and must never leave a deleted event visible.
        await ReloadAsync(preserveStatus: true);
        await RefreshCalendarAsync();
        await RefreshSchedulerAsync();
    }

    private void ResetEventConfirmation()
    {
        IsEventSaveConfirmationVisible = false;
    }

    public void BeginSchedulerEventCreate(int startDayIndex, int startMinuteOfDay, int endDayIndex, int endMinuteOfDay)
    {
        CancelTaskEdit();
        var weekStart = StartOfWeek(SelectedDate);
        startDayIndex = Math.Clamp(startDayIndex, 0, 6);
        // One day past the board: a drag carried below midnight on the last night of the week
        // ends on the morning after it, the same as on any other night.
        endDayIndex = Math.Clamp(endDayIndex, startDayIndex, CalendarDragRange.LastDayIndex);
        var date = weekStart.AddDays(startDayIndex);
        startMinuteOfDay = Math.Clamp(startMinuteOfDay, 0, 1439);
        endMinuteOfDay = Math.Clamp(endMinuteOfDay, 0, 1440);
        _eventEndDayOffset = endDayIndex - startDayIndex;
        ResetEventDrafts(allowed: true);
        _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        ResetEventConfirmation();
        EventTitle = string.Empty;
        EventDate = ToLocalOffset(date);
        EventTime = TimeSpan.FromMinutes(startMinuteOfDay);
        EventEndTime = TimeSpan.FromMinutes(endMinuteOfDay);
        EventActionText = localization.Text("AddEvent");
        RefreshDateLabels();
        IsScheduleExpanded = true;
        IsEventComposerVisible = true;
    }

    [RelayCommand]
    private async Task AskAssistantAsync()
    {
        var prompt = AssistantInput.Trim();
        if (prompt.Length == 0 || IsBusy) return;

        var attachment = _pendingAssistantAttachment;
        _pendingAssistantAttachment = null;
        Messages.Add(CreateUserMessage(prompt, attachment?.FileName));
        AssistantInput = string.Empty;
        Suggestions.Clear();
        AssistantActions.Clear();
        DynamicSuggestions.Clear();

        _assistantCancellation?.Cancel(); _assistantCancellation?.Dispose(); _assistantCancellation = new();
        _streamingMessage = null;
        IsBusy = true;
        AssistantStage = localization.Text("StageThinking");
        AssistantState = localization.Text("Thinking");
        try
        {
            await FlushSettingsAsync();
            var reply = await assistant.AskAsync(
                new AssistantAsk(prompt, ActiveConversationId, attachment), _assistantCancellation.Token);

            if (reply.ConversationId is { } conversationId && conversationId != ActiveConversationId)
            {
                ActiveConversationId = conversationId;
                await PersistActiveConversationAsync();
            }

            // The answer already on screen becomes the answer, rather than being replaced by an
            // identical copy of itself directly underneath.
            if (_streamingMessage is { } streamed)
            {
                streamed.Complete(reply.Message,
                    $"{reply.Provider} · {reply.Elapsed.TotalSeconds:0.0} {localization.Text("SecondsShort")}",
                    reply.IsError,
                    reply.Trace.Select(entry => new AssistantToolTraceViewModel(entry, localization)));
                _streamingMessage = null;
            }
            else Messages.Add(CreateAssistantMessage(reply));

            foreach (var suggestion in reply.Suggestions)
                Suggestions.Add(new(suggestion, item => RunBusyAsync(() => AddSuggestionAsync(item)), localization));

            var accessLevel = Parse(AssistantAccessLevel, Domain.AssistantAccessLevel.ConfirmAll);
            var appliedAny = reply.Trace.Any(entry => entry.Applied);
            foreach (var action in reply.ScheduleActions)
            {
                var item = new AssistantActionViewModel(action, ConfirmAssistantActionAsync, localization);
                AssistantActions.Add(item);
                // Tool-calling providers already applied whatever the access level allows;
                // this only covers models that answered with the legacy JSON envelope.
                if (reply.Trace.Count > 0 || !AssistantPermissionPolicy.MayApplyAutomatically(accessLevel, action)) continue;
                try
                {
                    await ApplyAssistantActionCoreAsync(item);
                    appliedAny = true;
                }
                catch (Exception exception)
                {
                    crashReporter.Record(exception, "Applying automatic assistant action");
                    item.MarkFailed();
                }
            }
            if (appliedAny)
            {
                await ReloadAsync(preserveStatus: true);
                await RefreshCalendarAsync();
                await RefreshSchedulerAsync();
            }

            _lastAttachment = null;
            var needsReview = AssistantActions.Any(item => item.IsPending) || reply.Suggestions.Count > 0;
            AssistantState = localization.Text(needsReview ? "ReviewSuggestions" : "ReplyReady");
            await RefreshConversationsAsync();
            await RefreshMemoriesAsync();
            await RefreshSuggestionsAsync();
        }
        catch (OperationCanceledException)
        {
            DiscardStreamingMessage();
            AssistantState = localization.Text("RequestCancelled");
        }
        catch (Exception ex)
        {
            DiscardStreamingMessage();
            crashReporter.Record(ex, "Assistant request");
            Messages.Add(new(localization.Format("ReplyFailed", ex.Message), false, isError: true));
            AssistantState = localization.Text("ConnectionError");
            // The file the user picked is theirs; a failed request should not throw it away.
            if (_lastAttachment is { } kept) SetAttachedFile(kept.Name, kept.Path);
        }
        finally
        {
            IsBusy = false;
            AssistantStage = string.Empty;
            _streamingMessage = null;
        }
    }

    [RelayCommand] private void CancelAssistant() => _assistantCancellation?.Cancel();

    private async Task AddSuggestionAsync(AssistantSuggestionViewModel item)
    {
        await taskRepository.AddAsync(FromSuggestion(item.Suggestion)); item.MarkAdded(); Status = localization.Text("SuggestionAdded");
        await ReloadAsync(preserveStatus: true);
    }

    private Task ConfirmAssistantActionAsync(AssistantActionViewModel item)
        => RunBusyAsync(async () =>
        {
            try
            {
                await ApplyAssistantActionCoreAsync(item);
                await RefreshCalendarAsync();
                await RefreshSchedulerAsync();
            }
            catch
            {
                item.MarkFailed();
                throw;
            }
        });

    private async Task ApplyAssistantActionCoreAsync(AssistantActionViewModel item)
    {
        item.IsRunning = true;
        try
        {
            // One shared executor performs the change for both the chat tool loop and this
            // confirm button, so the two paths can never drift apart.
            await actionExecutor.ApplyAsync(item.Action);
            item.MarkApplied();
            Status = localization.Text("AiActionApplied");
        }
        finally
        {
            item.IsRunning = false;
        }
    }

    [RelayCommand]
    private Task SaveSettingsAsync() => SaveSettingsCoreAsync(includeApiKey: true);

    public async Task FlushSettingsAsync()
    {
        _settingsSaveDelay?.Cancel();
        await SaveSettingsCoreAsync(includeApiKey: true);
    }

    private async Task SynchronizeAutostartAsync()
    {
        if (!autostartService.IsSupported)
        {
            SetAutostartState(false);
            if (_settings is not null) _settings.AutostartEnabled = false;
            return;
        }

        await _autostartGate.WaitAsync();
        try
        {
            // The saved preference is authoritative. Reapplying it also refreshes the
            // executable path if the portable application was moved after an update.
            await autostartService.SetEnabledAsync(AutostartEnabled);
            var actual = await autostartService.IsEnabledAsync();
            SetAutostartState(actual);
            if (_settings is not null) _settings.AutostartEnabled = actual;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Synchronizing system autostart");
            SettingsSaveState = localization.Text("AutostartFailed");
        }
        finally { _autostartGate.Release(); }
    }

    private async Task ApplyAutostartAsync(bool enabled, int revision)
    {
        if (!autostartService.IsSupported) return;
        await _autostartGate.WaitAsync();
        try
        {
            if (revision != Volatile.Read(ref _autostartChangeVersion)) return;
            await autostartService.SetEnabledAsync(enabled);
            var actual = await autostartService.IsEnabledAsync();
            if (revision != Volatile.Read(ref _autostartChangeVersion)) return;
            SetAutostartState(actual);
            if (_settings is not null) _settings.AutostartEnabled = actual;
            await SaveSettingsCoreAsync(includeApiKey: false);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Changing system autostart");
            try { SetAutostartState(await autostartService.IsEnabledAsync()); }
            catch (Exception stateException) { crashReporter.Record(stateException, "Reading system autostart"); }
            SettingsSaveState = localization.Text("AutostartFailed");
        }
        finally { _autostartGate.Release(); }
    }

    private void SetAutostartState(bool value)
    {
        if (AutostartEnabled == value) return;
        _synchronizingAutostartState = true;
        try { AutostartEnabled = value; }
        finally { _synchronizingAutostartState = false; }
    }

    private void ScheduleSettingsSave()
    {
        if (_loadingSettings || _settings is null) return;
        _settingsSaveDelay?.Cancel();
        _settingsSaveDelay?.Dispose();
        _settingsSaveDelay = new CancellationTokenSource();
        var token = _settingsSaveDelay.Token;
        SettingsSaveState = localization.Text("SettingsSaving");
        crashReporter.Diagnostic($"Settings save scheduled: language={SelectedLanguage?.Code}, theme={ShellTheme}");
        _ = SaveAfterDelayAsync(token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(550, cancellationToken);
            await SaveSettingsCoreAsync(includeApiKey: false, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Debounced settings save");
            SettingsSaveState = localization.Text("SettingsError");
        }
    }

    private async Task SaveSettingsCoreAsync(bool includeApiKey, CancellationToken cancellationToken = default)
    {
        if (_settings is null) return;
        await _settingsSaveGate.WaitAsync(cancellationToken);
        try
        {
            CopySettingsFromView(includeApiKey);
            await settingsRepository.UpdateAppSettingsAsync(_settings, cancellationToken);
            crashReporter.Diagnostic($"Settings saved: language={_settings.LanguageCode}, theme={_settings.ShellTheme}, section={_settings.LastSection}");
            themeService.Apply(_settings);
            ShellSettingsChanged?.Invoke(this, EventArgs.Empty);
            SettingsSaveState = localization.Text("SettingsSaved");
            // A new folder or client id changes what is still missing from the sync setup, and
            // the card should say so as soon as the change is stored rather than half a minute on.
            if (IsSettings && IsSettingsData) _ = RefreshCloudStatusAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Saving settings");
            SettingsSaveState = localization.Text("SettingsError");
        }
        finally { _settingsSaveGate.Release(); }
    }

    private void CopySettingsFromView(bool includeApiKey)
    {
        if (_settings is null) return;
        _settings.AssistantProvider = Parse(AssistantProvider, Domain.AssistantProvider.Local);
        _settings.AssistantAccessLevel = Parse(AssistantAccessLevel, Domain.AssistantAccessLevel.ConfirmAll);
        _settings.ShareContextWithExternalAi = ShareContextWithExternalAi;
        _settings.AssistantEndpoint = AssistantEndpoint.Trim();
        _settings.AssistantModel = AssistantModel.Trim();
        if (includeApiKey && !string.IsNullOrWhiteSpace(AssistantApiKey))
        {
            _settings.AssistantApiKeyCiphertext = secrets.Protect(AssistantApiKey.Trim());
            AssistantApiKey = string.Empty;
            HasSavedApiKey = true;
        }
        _settings.McpTransport = McpTransport;
        _settings.McpEndpoint = McpEndpoint.Trim();
        _settings.McpCommand = McpCommand.Trim();
        _settings.McpArguments = McpArguments.Trim();
        _settings.McpToolName = McpToolName.Trim();
        _settings.McpToolsEnabled = McpToolsEnabled;
        _settings.AssistantMemoryEnabled = AssistantMemoryEnabled;
        _settings.ActiveConversationId = ActiveConversationId;
        _settings.LocalModelId = LocalModelCatalog.IsCustom(LocalModelId)
            ? LocalModelCatalog.CustomModelId
            : LocalModelCatalog.Find(LocalModelId).Id;
        _settings.LocalModelContextTokens = RuntimeOptions.ContextTokens;
        _settings.LocalModelThreads = RuntimeOptions.Threads;
        _settings.LocalModelAutoTune = LocalModelAutoTune;
        _settings.CustomModelPath = CustomModelPath.Trim();
        _settings.CustomProjectorPath = CustomProjectorPath.Trim();
        CopyGenerationParameters(_settings);
        _settings.AssistantExtraInstructions = AssistantExtraInstructions.Trim();
        _settings.LocalModelVisionEnabled = LocalModelVisionEnabled;
        _settings.LocalModelGpuEnabled = LocalModelGpuEnabled;
        _settings.LocalModelIdleMinutes = ParseLocalModelIdleMinutes();
        _settings.ShellTheme = Domain.ShellTheme.Marelian;
        _settings.ShellThemeMode = Parse(ShellThemeMode, Domain.ShellThemeMode.Dark);
        _settings.ThemeColorSource = Parse(ThemeColorSource, Domain.ThemeColorSource.Preset);
        _settings.VisualPalette = Parse(VisualPalette, Domain.VisualPalette.Vibrant);
        _settings.AppFontFamily = AppFontFamily;
        _settings.CustomAccent = NormalizeColor(CustomAccent, "#D75053");
        _settings.CustomBackground = NormalizeColor(CustomBackground, "#13110F");
        // Nothing stored means "follow the theme", which is what a fresh profile does.
        _settings.MascotColor = UseThemeMascotColor ? string.Empty : NormalizeColor(MascotColor, "#D75053");
        _settings.ShowMascot = ShowMascot;
        SaveActiveThemeColors();
        _settings.ThemeColorOverridesJson = MarelianAppearance.Write(_themeColorOverrides);
        _settings.EnableTransparency = EnableTransparency;
        _settings.TransparencyIntensity = Math.Clamp(TransparencyIntensity, 0, 80);
        // Hover no longer opens anything; the handle answers the left button with the panel
        // and the right button with the quick widget.
        _settings.RevealMode = EdgeRevealMode.Click;
        _settings.WidgetHideDelayMs = (int)Math.Round(Math.Clamp(WidgetHideDelayMs, 400, 6000));
        _settings.WidgetKeepOpen = WidgetKeepOpen;
        _settings.ShortcutsJson = ShellShortcuts.Write(_shortcuts);
        _settings.PinOpen = PinOpen;
        _settings.AutoPlan = false;
        _settings.LanguageCode = SelectedLanguage?.Code ?? "en";
        _settings.ReduceMotion = ReduceMotion;
        _settings.AutostartEnabled = AutostartEnabled;
        _settings.StartHidden = StartHidden;
        _settings.KeepOnTop = KeepOnTop;
        _settings.Use24HourClock = Use24HourClock;
        CopySystemSettings();
        _settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    [RelayCommand]
    private async Task ClearApiKeyAsync()
    {
        if (_settings is null) return;
        _settings.AssistantApiKeyCiphertext = null; AssistantApiKey = string.Empty; HasSavedApiKey = false;
        await SaveSettingsCoreAsync(includeApiKey: false); Status = localization.Text("SettingsSaved");
    }

    [RelayCommand]
    private async Task TestAssistantAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await SaveSettingsCoreAsync(includeApiKey: true);
            var result = await assistant.TestConnectionAsync();
            AssistantState = result.Message;
            Status = result.Message;
            await LoadAvailableToolsAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Testing assistant connection");
            AssistantState = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task SelectDateAsync(DateOnly date)
    {
        // Ticks belong to the day they were made on. Carrying them to another day would leave
        // a hidden selection that the next "delete selected" would act on.
        if (date != SelectedDate && (IsSelectionMode || IsMovePanelOpen)) ExitSelectionMode();
        SelectedDate = date;
        CalendarDate = date.ToDateTime(TimeOnly.MinValue);
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        EventDate = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        await ReloadDayAsync();
        BuildWeek();
        RefreshDateLabels();
        await RefreshSchedulerAsync();
    }

    private async Task SelectDateSafelyAsync(DateOnly date)
    {
        var version = Interlocked.Increment(ref _dateSelectionVersion);
        try
        {
            await SelectDateAsync(date);
            if (version != _dateSelectionVersion) return;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Selecting calendar date");
            Status = exception.Message;
        }
    }

    /// <summary>
    /// A panel that has just been asked for is arriving, and the arrival is a transition driven
    /// on the UI thread. This completes once it has landed, so whatever the panel needs filling
    /// with can be read now and written into the interface afterwards rather than during.
    /// </summary>
    private static Task RevealSettled() => Task.Delay(WindowRevealAnimator.EnterDuration);

    /// <summary>
    /// The same wait for a panel that is taking another one's place. Two panels are never on
    /// screen together, so a switch is the one leaving and the one arriving end to end — and the
    /// arrival does not even begin until the departure in front of it has finished.
    /// </summary>
    private static Task SwapSettled() => Task.Delay(WindowRevealAnimator.SwapDuration);

    /// <summary>
    /// Rebuilds the month grid. <paramref name="settled"/> is how a panel that is still arriving
    /// keeps the two apart: the read happens at once, off the UI thread, and the rebuild — which
    /// is UI-thread work — waits for the arrival to finish. Done inside it, the panel came in in
    /// steps instead of in one move.
    /// </summary>
    private async Task RefreshCalendarAsync(Task? settled = null)
    {
        try
        {
            var offset = ((int)_calendarMonth.DayOfWeek + 6) % 7;
            var firstVisible = _calendarMonth.AddDays(-offset);
            var lastVisibleExclusive = firstVisible.AddDays(42);
            var (rangeStart, _) = DayRange(firstVisible);
            var (_, rangeEnd) = DayRange(lastVisibleExclusive.AddDays(-1));
            // SQLite answers out of a local file, which means it answers synchronously: awaited
            // on the UI thread this is not a wait at all, it is the interface standing still.
            // The read leaves the thread only when there is an arrival to protect, because that
            // is also the only time there is certainly a message loop to come back to: startup
            // and the snapshot sweep both run before one exists, and an await that really yields
            // there posts its continuation to a dispatcher nobody is running.
            async Task<HashSet<DateOnly>> ReadMonthAsync()
            {
                var events = await calendarRepository.GetRangeAsync(rangeStart, rangeEnd);
                var occupied = new HashSet<DateOnly>();
                foreach (var calendarEvent in events)
                {
                    var eventStart = DateOnly.FromDateTime(calendarEvent.StartUtc.ToLocalTime().Date);
                    var eventEnd = DateOnly.FromDateTime(calendarEvent.EndUtc.AddTicks(-1).ToLocalTime().Date);
                    if (eventStart < firstVisible) eventStart = firstVisible;
                    if (eventEnd >= lastVisibleExclusive) eventEnd = lastVisibleExclusive.AddDays(-1);
                    for (var date = eventStart; date <= eventEnd; date = date.AddDays(1))
                        occupied.Add(date);
                }
                return occupied;
            }

            HashSet<DateOnly> occupiedDates;
            if (settled is null)
            {
                occupiedDates = await ReadMonthAsync();
            }
            else
            {
                occupiedDates = await Task.Run(ReadMonthAsync);
                await settled;
                // The panel that asked for this was switched away from while it was arriving.
                // Landing the rebuild anyway spends a full layout on the thread the panel that
                // replaced it is animating in on — which is what made every press in a quick
                // run of them slower than the one before.
                if (!IsCalendarPopupOpen) return;
            }

            CalendarMonthTitle = localization.CurrentCulture.TextInfo.ToTitleCase(
                _calendarMonth.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", localization.CurrentCulture));
            RebuildCalendarWeekDayNames();

            var selected = _calendarSelectionPurpose switch
            {
                CalendarSelectionPurpose.Event when EventDate is { } eventDate => DateOnly.FromDateTime(eventDate.LocalDateTime),
                CalendarSelectionPurpose.TaskDeadline when EditDeadline is { } deadline => DateOnly.FromDateTime(deadline.LocalDateTime),
                CalendarSelectionPurpose.TaskEarliestStart when EditEarliestStart is { } earliest => DateOnly.FromDateTime(earliest.LocalDateTime),
                CalendarSelectionPurpose.TaskBlock when TaskBlockDate is { } blockDate => DateOnly.FromDateTime(blockDate.LocalDateTime),
                CalendarSelectionPurpose.MoveTarget => MoveTargetDate,
                CalendarSelectionPurpose.ProjectDraftDay or CalendarSelectionPurpose.ProjectStepDay => _projectPickerDate,
                _ => SelectedDate
            };
            // Replaced cell by cell rather than emptied and refilled: clearing takes all
            // forty-two containers with it and the grid collapses and rebuilds itself in front
            // of the reader, which on a run of quick switches is most of what they see.
            var days = new CalendarDayViewModel[42];
            for (var index = 0; index < 42; index++)
            {
                var date = firstVisible.AddDays(index);
                days[index] = new(date, _calendarMonth, selected, occupiedDates.Contains(date), SelectCalendarDayAsync);
            }

            Refill(CalendarDays, days);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Refreshing calendar flyout");
            Status = exception.Message;
        }
    }

    /// <summary>
    /// The day names down the top of the month grid. They say the same thing every month, so
    /// they are rebuilt only when the language they are written in changes.
    /// </summary>
    private void RebuildCalendarWeekDayNames()
    {
        var culture = localization.CurrentCulture.Name;
        if (_calendarWeekDayCulture == culture && CalendarWeekDays.Count == 7) return;
        _calendarWeekDayCulture = culture;
        CalendarWeekDays.Clear();
        for (var index = 0; index < 7; index++)
        {
            var day = (DayOfWeek)(((int)DayOfWeek.Monday + index) % 7);
            CalendarWeekDays.Add(localization.CurrentCulture.DateTimeFormat.GetShortestDayName(day));
        }
    }

    /// <summary>
    /// Rebuilds the week board. <paramref name="settled"/> holds the rebuild back until the panel
    /// that asked for it has finished arriving; see <see cref="RefreshCalendarAsync"/>.
    /// </summary>
    private async Task RefreshSchedulerAsync(Task? settled = null)
    {
        try
        {
            // The hour rail says the same thing every week and in every panel state, so it is
            // built whether or not anyone is looking: standing there, it is one of the things
            // the panel's warm-up gets to pay for at idle rather than on the click.
            RebuildSchedulerHourLabels();

            // The week itself belongs to the calendar panel and to nothing else, so one nobody
            // has asked to see is not worth building. It used to be rebuilt at startup and on
            // every change to the day, which meant a busy week was paid for in full before the
            // panel had ever been opened - and paid again, on the first warm-up, as one long
            // layout. Opening the panel is what asks for it; see OpenCalendar.
            if (!IsCalendarPopupOpen) return;

            const double hourHeight = 52;
            const double dayWidth = 78;
            const double oneCycleHeight = 24 * hourHeight;
            var weekStart = StartOfWeek(SelectedDate);
            var weekEnd = weekStart.AddDays(6);

            var (rangeStart, _) = DayRange(weekStart);
            var (_, rangeEnd) = DayRange(weekStart.AddDays(6));
            // Off the thread only when there is an arrival to protect; see RefreshCalendarAsync.
            var events = settled is null
                ? await calendarRepository.GetRangeAsync(rangeStart, rangeEnd)
                : await Task.Run(() => calendarRepository.GetRangeAsync(rangeStart, rangeEnd));
            if (settled is not null)
            {
                await settled;
                // Switched away from while it was arriving; see RefreshCalendarAsync.
                if (!IsCalendarPopupOpen) return;
            }

            SchedulerWeekTitle = $"{weekStart.ToString("d MMM", localization.CurrentCulture)} — {weekEnd.ToString("d MMM yyyy", localization.CurrentCulture)}";

            var weekDays = new SchedulerDayViewModel[7];
            for (var day = 0; day < 7; day++)
                weekDays[day] = new(weekStart.AddDays(day), SelectedDate, SelectDateSafelyAsync, localization);
            Refill(SchedulerWeekDays, weekDays);

            // Built into a plain list and swapped in position by position at the end. Clearing
            // the bound collection first took every block container on the board down with it
            // and the layout closed over the hole before the replacements arrived, so the week
            // fell in on itself and built itself back up on every switch onto this panel.
            var blocks = new List<SchedulerBlockViewModel>();

            void AddBlock(string title, DateTimeOffset startUtc, DateTimeOffset endUtc, Avalonia.Media.IBrush accent, Avalonia.Media.IBrush accentGlow,
                CalendarEvent? calendarEvent, TimeBlock? timeBlock)
            {
                var localStart = startUtc.ToLocalTime();
                var localEnd = endUtc.ToLocalTime();
                for (var dayIndex = 0; dayIndex < 7; dayIndex++)
                {
                    var date = weekStart.AddDays(dayIndex);
                    var visibleStart = ToLocalOffset(date);
                    var visibleEnd = ToLocalOffset(date).AddHours(24);
                    var segmentStart = localStart > visibleStart ? localStart : visibleStart;
                    var segmentEnd = localEnd < visibleEnd ? localEnd : visibleEnd;
                    if (segmentEnd <= segmentStart) continue;
                    var minutesFromStart = (segmentStart - visibleStart).TotalMinutes;
                    var durationMinutes = (segmentEnd - segmentStart).TotalMinutes;
                    var baseTop = minutesFromStart / 60d * hourHeight + 1;
                    var blockHeight = Math.Max(24, durationMinutes / 60d * hourHeight - 3);
                    // Only the slice that is the whole item can be dragged; see the parameter.
                    var isWholeItem = segmentStart == localStart && segmentEnd == localEnd;

                    for (var cycle = 0; cycle < 3; cycle++)
                    {
                        blocks.Add(new(title, startUtc, endUtc, accent, accentGlow,
                            dayIndex * dayWidth + 2,
                            baseTop + cycle * oneCycleHeight,
                            dayWidth - 4,
                            blockHeight,
                            calendarEvent,
                            timeBlock,
                            isWholeItem,
                            BeginEventEditAsync,
                            OpenTaskBlockAsync,
                            Use24HourClock,
                            !ReduceMotion,
                            localization));
                    }
                }
            }

            foreach (var item in events)
                AddBlock(item.Title, item.StartUtc, item.EndUtc, EventVisuals.AccentBrush(item.Category), EventVisuals.AccentGlowBrush(item.Category), item, null);

            FillSchedulerBoard(blocks);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Refreshing weekly scheduler");
            Status = exception.Message;
        }
    }

    // ---- The week board's cost ---------------------------------------------------------
    // The board is three stacked copies of one day, and the scroll offset is wrapped back into
    // the middle copy as the user goes past midnight in either direction (see
    // SidePanelWindow.NormalizeScrollOffset), which is what makes the timeline endless. Three
    // copies also means three buttons per event, and every button is a small tree of controls
    // matched against the whole of the application's styles - which on a busy week was several
    // seconds of the user interface standing still the first time the panel was asked for.

    /// <summary>One copy of the day, in board pixels: twenty-four hours at fifty-two each.</summary>
    private const double SchedulerCycleHeight = 24 * 52d;

    /// <summary>
    /// How many blocks go onto the board in the first turn and in each turn after it, and how
    /// long those turns wait for each other. A row added to the bound collection is not the
    /// work: the work is the button built for it when the board is next laid out, which is why
    /// this is paced on a frame rather than on how long the additions themselves take.
    ///
    /// The first turn is larger because it is the one the reader is waiting on - it carries the
    /// hours the board opens at - and because a window arriving with a visibly empty grid in it
    /// is worse than a window that takes a moment longer to arrive.
    /// </summary>
    private const int SchedulerFillLead = 24;
    private const int SchedulerFillChunk = 8;
    private static readonly TimeSpan SchedulerFillInterval = TimeSpan.FromMilliseconds(16);

    private IReadOnlyList<SchedulerBlockViewModel>? _schedulerFillPending;
    private int _schedulerFillIndex;
    private DispatcherTimer? _schedulerFillTimer;

    /// <summary>
    /// Where the board wants to open: the top of the earliest thing in the week, in the middle
    /// copy of the timeline. Worked out as the week is built rather than read back off the
    /// board, because the board is still filling at the moment the panel has to place itself -
    /// and a board that jumps once it has finished arriving is worse than one that never moved.
    /// </summary>
    public double? SchedulerFocusTop { get; private set; }

    /// <summary>Raised once the week is known, which is when the panel can place its scroll.</summary>
    public event EventHandler? SchedulerWeekReady;

    /// <summary>
    /// The order the week goes onto the board in. Nothing about the board depends on it - every
    /// block is placed by its own coordinates - so the fill is free to start where the reader is
    /// looking: the middle copy, from its earliest hour down. The other two copies exist for the
    /// wrap at midnight and can arrive whenever they like.
    ///
    /// It has to be a total order all the same. The blocks that are already standing are matched
    /// against the new ones position by position, so two builds of an unchanged week must come
    /// out in exactly the same order or every one of them counts as a change.
    /// </summary>
    private static int CompareFillOrder(SchedulerBlockViewModel first, SchedulerBlockViewModel second)
    {
        static int Rank(double top) => top >= 2 * SchedulerCycleHeight ? 2 : top >= SchedulerCycleHeight ? 0 : 1;

        var byCycle = Rank(first.Top).CompareTo(Rank(second.Top));
        if (byCycle != 0) return byCycle;
        var byTop = first.Top.CompareTo(second.Top);
        if (byTop != 0) return byTop;
        var byLeft = first.Left.CompareTo(second.Left);
        return byLeft != 0 ? byLeft : string.CompareOrdinal(first.Title, second.Title);
    }

    /// <summary>
    /// Puts the week on the board a few blocks at a time. Handing a bound collection two or
    /// three hundred rows in one go is one long turn of the user interface thread - the window
    /// is already on screen by then, so what it looks like is the application hanging. Spread
    /// over dispatcher turns the same work costs the same total and blocks nothing: the panel
    /// arrives at once and the week lands on it over the following frames.
    /// </summary>
    private void FillSchedulerBoard(List<SchedulerBlockViewModel> blocks)
    {
        blocks.Sort(CompareFillOrder);
        SchedulerFocusTop = blocks.FirstOrDefault() is { } first
                            && first.Top >= SchedulerCycleHeight && first.Top < 2 * SchedulerCycleHeight
            ? first.Top
            : null;
        SchedulerWeekReady?.Invoke(this, EventArgs.Empty);

        // Whatever the new week has no room for goes first. A block left over from last week
        // standing on the board while the rest arrives is the one thing a paced fill must not
        // show, and removing a row is far cheaper than building one.
        while (SchedulerBlocks.Count > blocks.Count) SchedulerBlocks.RemoveAt(SchedulerBlocks.Count - 1);
        _schedulerFillPending = blocks;
        _schedulerFillIndex = 0;

        _schedulerFillTimer ??= new DispatcherTimer(SchedulerFillInterval, DispatcherPriority.Background,
            (_, _) => FillSchedulerSlice(SchedulerFillChunk));
        // The opening hours go on straight away - and a week that has not changed, which is most
        // of them, finishes here without a single button being rebuilt.
        FillSchedulerSlice(SchedulerFillLead);
    }

    private void FillSchedulerSlice(int budget)
    {
        if (_schedulerFillPending is not { } blocks) { _schedulerFillTimer?.Stop(); return; }

        var built = 0;
        while (_schedulerFillIndex < blocks.Count)
        {
            var fresh = blocks[_schedulerFillIndex];
            if (_schedulerFillIndex < SchedulerBlocks.Count)
            {
                // Almost every refresh rebuilds a week that has not changed; see IRowEquivalence.
                // A row left standing costs nothing, so only real changes count against the
                // budget - which is what lets an unchanged week land in a single turn.
                if (!SchedulerBlocks[_schedulerFillIndex].Matches(fresh))
                {
                    SchedulerBlocks[_schedulerFillIndex] = fresh;
                    built++;
                }
            }
            else
            {
                SchedulerBlocks.Add(fresh);
                built++;
            }

            _schedulerFillIndex++;
            if (built >= budget) break;
        }

        if (_schedulerFillIndex < blocks.Count)
        {
            if (_schedulerFillTimer is { IsEnabled: false } timer) timer.Start();
            return;
        }

        _schedulerFillPending = null;
        _schedulerFillTimer?.Stop();
    }

    /// <summary>
    /// Builds the week and puts the whole of it on the board at once. The offscreen snapshot
    /// sweep has no panel to open and photographs the board the moment it asks for it, so it
    /// needs both halves of what opening the calendar normally does.
    /// </summary>
    public async Task SettleSchedulerBoardAsync()
    {
        await RefreshSchedulerAsync();
        FillSchedulerSlice(int.MaxValue);
    }

    /// <summary>
    /// The hour rail down the side of the board. It says the same thing every week, so it is
    /// rebuilt only when the thing it depends on changes — the clock format or the language.
    /// Rebuilt on every refresh, its seventy-three labels were torn down and made again each
    /// time the calendar was opened or the week was stepped through.
    /// </summary>
    private void RebuildSchedulerHourLabels()
    {
        const double hourHeight = 52;
        var format = $"{(Use24HourClock ? 24 : 12)}:{localization.CurrentCulture.Name}";
        if (_schedulerHourFormat == format && SchedulerHours.Count == 3 * 24 + 1) return;
        _schedulerHourFormat = format;
        SchedulerHours.Clear();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                SchedulerHours.Add(new(ClockText.Format(TimeSpan.FromHours(hour), Use24HourClock, localization.CurrentCulture),
                    (cycle * 24 + hour) * hourHeight));
            }
        }

        SchedulerHours.Add(new(ClockText.Format(TimeSpan.Zero, Use24HourClock, localization.CurrentCulture),
            3 * 24 * hourHeight));
    }

    private async Task SelectCalendarDayAsync(DateOnly date)
    {
        var closeAfterSelection = true;
        switch (_calendarSelectionPurpose)
        {
            case CalendarSelectionPurpose.Event:
                EventDate = ToLocalOffset(date);
                IsEventDatePickerOpen = false;
                break;
            case CalendarSelectionPurpose.DayCopy:
                IsEventDatePickerOpen = false;
                RequestCopyDay(date.DayNumber - SelectedDate.DayNumber);
                break;
            case CalendarSelectionPurpose.EventCopy:
                IsEventDatePickerOpen = false;
                if (_eventToCopy is not null)
                {
                    var sourceDate = DateOnly.FromDateTime(_eventToCopy.StartUtc.ToLocalTime().Date);
                    _copyDayOffsetDays = date.DayNumber - sourceDate.DayNumber;
                    EventCopyConfirmationText = localization.Format("CopyEventConfirmation", _eventToCopy.Title, date.ToString("d MMMM", localization.CurrentCulture));
                    IsEventCopyConfirmationVisible = true;
                }
                break;
            case CalendarSelectionPurpose.MoveTarget:
                IsEventDatePickerOpen = false;
                MoveTargetDate = date;
                MoveTargetDateLabel = date.ToString("ddd, d MMM", localization.CurrentCulture);
                IsMoveByOffset = false;
                RefreshMovePreview();
                break;
            case CalendarSelectionPurpose.DayShiftTarget:
                IsEventDatePickerOpen = false;
                SetDayShiftTarget(date);
                // Naming a day is the answer, so the shift stops being an amount.
                IsDayShiftByDate = true;
                IsDayShiftMoreOpen = true;
                break;
            case CalendarSelectionPurpose.TaskDeadline:
                EditDeadline = ToLocalOffset(date).AddHours(23).AddMinutes(59);
                IsTaskDatePickerOpen = false;
                break;
            case CalendarSelectionPurpose.TaskEarliestStart:
                EditEarliestStart = ToLocalOffset(date);
                IsTaskDatePickerOpen = false;
                break;
            case CalendarSelectionPurpose.TaskBlock:
                TaskBlockDate = ToLocalOffset(date);
                IsTaskDatePickerOpen = false;
                break;
            case CalendarSelectionPurpose.ProjectDraftDay or CalendarSelectionPurpose.ProjectStepDay:
                IsEventDatePickerOpen = false;
                await ApplyProjectDateAsync(date);
                break;
            default:
                await SelectDateSafelyAsync(date);
                _calendarMonth = new DateOnly(date.Year, date.Month, 1);
                await RefreshCalendarAsync();
                closeAfterSelection = false;
                break;
        }
        RefreshDateLabels();
        if (closeAfterSelection && _calendarSelectionPurpose != CalendarSelectionPurpose.TaskBlock)
            IsCalendarPopupOpen = false;
    }

    private void RefreshDateLabels()
    {
        SelectedDateLabel = SelectedDate.ToString("ddd, d MMM", localization.CurrentCulture);
        EventDateLabel = (EventDate is { } eventDate ? DateOnly.FromDateTime(eventDate.LocalDateTime) : SelectedDate)
            .ToString("ddd, d MMM", localization.CurrentCulture);
        EditDeadlineLabel = EditDeadline is { } deadline
            ? DateOnly.FromDateTime(deadline.LocalDateTime).ToString("d MMM yyyy", localization.CurrentCulture)
            : localization.Text("NoDeadline");
        EditEarliestStartLabel = EditEarliestStart is { } earliest
            ? DateOnly.FromDateTime(earliest.LocalDateTime).ToString("d MMM yyyy", localization.CurrentCulture)
            : localization.Text("AnyDate");
        TaskBlockDateLabel = TaskBlockDate is { } blockDate
            ? DateOnly.FromDateTime(blockDate.LocalDateTime).ToString("ddd, d MMM", localization.CurrentCulture)
            : "—";
        UpdateCounters();
    }

    /// <summary>Formats counters with the plural rules needed by the active interface language.</summary>
    private string FormatEventsCount(int count)
        => localization.Format(PluralKey(count, "EventsCountOne", "EventsCountFew", "EventsCountMany"), count);

    private string PluralKey(int count, string one, string few, string many)
    {
        var language = localization.CurrentCulture.TwoLetterISOLanguageName;
        var lastTwo = count % 100;
        var last = count % 10;

        if (language is "ru" or "uk")
        {
            if (lastTwo is >= 11 and <= 19) return many;
            return last == 1 ? one : last is >= 2 and <= 4 ? few : many;
        }

        if (language == "pl")
        {
            if (count == 1) return one;
            return last is >= 2 and <= 4 && lastTwo is not (>= 12 and <= 14) ? few : many;
        }

        return count == 1 ? one : many;
    }

    private string FormatItemsCount(int count)
        => localization.Format(PluralKey(count, "ItemsCountOne", "ItemsCountFew", "ItemsCountMany"), count);

    private void UpdateCounters()
    {
        TasksCountText = FormatItemsCount(Tasks.Count);
        ScheduleCountText = FormatItemsCount(Agenda.Count);
    }

    private async Task ReloadAsync(bool preserveStatus = false)
    {
        await ReloadTasksAsync();
        await ReloadDayAsync(); BuildWeek();
        await RefreshSchedulerAsync();
        UpdateCounters();
        if (!preserveStatus) Status = localization.Text("StatusReady");
    }

    /// <summary>
    /// Rebuilds the unscheduled list. A row is kept for as long as its task exists and brought up
    /// to date where it stands: a task broken into steps carries the box its next step is being
    /// typed in, and a row thrown away and built again would take the caret with it.
    ///
    /// Unfinished first; among those, the nearest deadline at the top and the ones without a
    /// deadline after every one that has — a task due on Friday outranks one due whenever.
    /// </summary>
    private async Task ReloadTasksAsync()
    {
        var allTasks = await taskRepository.GetAllAsync();
        var rows = new List<CompactTaskViewModel>(allTasks.Count);
        foreach (var task in allTasks
                     .OrderBy(task => task.Status == DomainTaskStatus.Done)
                     .ThenBy(task => task.DeadlineUtc is null)
                     .ThenBy(task => task.DeadlineUtc)
                     .ThenByDescending(task => task.Priority)
                     .ThenBy(task => task.CreatedAtUtc))
        {
            var row = _taskRows.TryGetValue(task.Id, out var standing) ? standing : null;
            if (row is null || row.Use24HourClock != Use24HourClock)
            {
                row = new CompactTaskViewModel(task, item => RunBusyAsync(() => ToggleTaskAsync(item)), BeginTaskEditAsync,
                    item => RunBusyAsync(() => DeleteTaskAsync(item)), BreakTaskIntoStepsAsync, localization, Use24HourClock);
                _taskRows[task.Id] = row;
            }
            else row.Update(task);

            row.Project = _taskProjects.GetValueOrDefault(task.Id);
            rows.Add(row);
        }

        foreach (var gone in _taskRows.Keys.Where(id => rows.All(row => row.Task.Id != id)).ToList()) _taskRows.Remove(gone);
        SyncInOrder(Tasks, rows);
        UpdateCounters();
    }

    private async Task ReloadDayAsync()
    {
        var (start, end) = DayRange(SelectedDate);
        var events = (await calendarRepository.GetRangeAsync(start, end)).OrderBy(item => item.StartUtc).ToList();
        // The day is rebuilt every minute, and every row with it. A tray that was open - which
        // now means a question that was being answered - used to be swept away by that rebuild,
        // so recording a verdict was a race against the clock. The rows are new objects; what
        // was open about them is not.
        var openTrays = Agenda.Where(item => item.IsTrackingOpen && item.Event is not null)
            .Select(item => item.Event!.Id).ToHashSet();
        Agenda.Clear();
        foreach (var calendarEvent in events)
        {
            var row = new AgendaItemViewModel(calendarEvent, BeginEventEditAsync, RequestCopyEventAsync,
                (item, scope) => RunBusyAsync(() => DeleteEventAsync(item, scope)), localization, Use24HourClock, !ReduceMotion);
            AttachRowTracking(row);
            AttachRowSelection(row);
            AttachRowSteps(row);
            row.IsTrackingOpen = openTrays.Contains(calendarEvent.Id);
            Agenda.Add(row);
        }
        // A reminder marks its event for a few seconds, and the day is rebuilt every minute.
        // Without this the mark would survive only until the next rebuild caught it.
        ApplyEventFlash();
        UpdateCounters();
        DateTitle = SelectedDate.ToString("dddd, d MMMM", localization.CurrentCulture);
        DateTitle = localization.CurrentCulture.TextInfo.ToTitleCase(DateTitle);
        var total = events.Sum(calendarEvent =>
        {
            var clippedStart = calendarEvent.StartUtc > start ? calendarEvent.StartUtc : start;
            var clippedEnd = calendarEvent.EndUtc < end ? calendarEvent.EndUtc : end;
            return clippedEnd > clippedStart ? (int)Math.Ceiling((clippedEnd - clippedStart).TotalMinutes) : 0;
        });

        // Verdicts first: the day progress, the card fallback and every row chip read from them.
        await LoadDayOutcomesAsync(events);
        ApplyDayOutcomes();

        DaySummary = Agenda.Count == 0
            ? localization.Text("NoEventsForDay")
            : localization.Format("EventDaySummaryFormat", Agenda.Count, total);
        // Tallies the day, which is what the card, the bar and the widget all read from.
        ApplyTrackingTarget(events, SelectedDate == DateOnly.FromDateTime(DateTime.Today), DateTimeOffset.UtcNow);
        RefreshDayProgress();
        RefreshSelectionSummary();
        RefreshPeekSummary();
        // The wrap-up is always about today, whichever day is on screen, so it is rebuilt from
        // the calendar rather than from the agenda this method just laid out.
        await RefreshWrapUpAsync();
        // Projects ride on the day: a verdict just recorded on a booked step's event ticks the
        // step, and the rows above learn which of them are a project's slots.
        await ReloadProjectsAsync();
    }

    private void BuildWeek()
    {
        WeekDays.Clear(); var mondayOffset = ((int)SelectedDate.DayOfWeek + 6) % 7; var monday = SelectedDate.AddDays(-mondayOffset);
        for (var index = 0; index < 7; index++) WeekDays.Add(new(monday.AddDays(index), SelectedDate, SelectDateSafelyAsync, localization));
    }

    private static DateOnly StartOfWeek(DateOnly date)
        => CalendarWeek.Start(date);

    private void LoadSettings(AppSettings settings)
    {
        LoadThemeColorOverrides(settings);
        LoadSystemSettings(settings);
        LoadShortcuts(settings);
        AssistantProvider = settings.AssistantProvider.ToString(); AssistantAccessLevel = settings.AssistantAccessLevel.ToString();
        ShareContextWithExternalAi = settings.ShareContextWithExternalAi; AssistantEndpoint = settings.AssistantEndpoint;
        AssistantModel = settings.AssistantModel; HasSavedApiKey = !string.IsNullOrWhiteSpace(settings.AssistantApiKeyCiphertext);
        McpTransport = settings.McpTransport; McpEndpoint = settings.McpEndpoint;
        McpCommand = settings.McpCommand; McpArguments = settings.McpArguments; McpToolName = settings.McpToolName;
        McpToolsEnabled = settings.McpToolsEnabled; AssistantMemoryEnabled = settings.AssistantMemoryEnabled;
        LocalModelId = LocalModelCatalog.IsCustom(settings.LocalModelId)
            ? LocalModelCatalog.CustomModelId
            : LocalModelCatalog.Find(settings.LocalModelId).Id;
        LocalModelContextText = Math.Clamp(settings.LocalModelContextTokens, 2048, 131_072).ToString();
        LocalModelThreadsText = Math.Max(0, settings.LocalModelThreads).ToString();
        LocalModelAutoTune = settings.LocalModelAutoTune;
        CustomModelPath = settings.CustomModelPath;
        CustomProjectorPath = settings.CustomProjectorPath;
        LoadGenerationParameters(settings);
        AssistantExtraInstructions = settings.AssistantExtraInstructions;
        LocalModelVisionEnabled = settings.LocalModelVisionEnabled;
        LocalModelGpuEnabled = settings.LocalModelGpuEnabled;
        ShellTheme = nameof(Domain.ShellTheme.Marelian);
        ShellThemeMode = settings.ShellThemeMode.ToString();
        ThemeColorSource = settings.ThemeColorSource.ToString();
        VisualPalette = settings.VisualPalette.ToString();
        AppFontFamily = string.IsNullOrWhiteSpace(settings.AppFontFamily) ? FontCatalogService.InterId : settings.AppFontFamily;
        CustomAccent = settings.CustomAccent; CustomBackground = settings.CustomBackground; EnableTransparency = settings.EnableTransparency;
        MascotColor = settings.MascotColor ?? string.Empty;
        UseThemeMascotColor = string.IsNullOrWhiteSpace(settings.MascotColor);
        ShowMascot = settings.ShowMascot;
        TransparencyIntensity = Math.Clamp(settings.TransparencyIntensity, 0, 80);
        RevealMode = nameof(EdgeRevealMode.Click); PinOpen = settings.PinOpen;
        WidgetHideDelayMs = Math.Clamp(
            settings.WidgetHideDelayMs <= 0 ? 1500 : settings.WidgetHideDelayMs, 400, 6000);
        WidgetKeepOpen = settings.WidgetKeepOpen;
        SelectedLanguage = LanguageOptions.FirstOrDefault(language => language.Code == settings.LanguageCode)
                           ?? LanguageOptions.First(language => language.Code == "en");
        ReduceMotion = settings.ReduceMotion; AutostartEnabled = settings.AutostartEnabled;
        StartHidden = settings.StartHidden; KeepOnTop = settings.KeepOnTop;
        Use24HourClock = settings.Use24HourClock;
        SelectedLocalModelIdleOption = new ChoiceOption(Math.Max(0, settings.LocalModelIdleMinutes).ToString(), string.Empty);
        LoadAppearanceControls(_themeColorOverrides.GetValueOrDefault(_activeThemeColorKey!) ?? new());
    }

    private void BuildLocalizedChoices()
    {
        var theme = ShellTheme;
        var themeMode = ShellThemeMode;
        var colorSource = ThemeColorSource;
        var visualPalette = VisualPalette;
        var appFontFamily = AppFontFamily;
        var provider = AssistantProvider;
        var accessLevel = AssistantAccessLevel;
        var transport = McpTransport;
        var eventCategory = _eventCategoryDraft.ToString();
        var taskCategory = _taskCategoryDraft.ToString();
        var eventPriority = _eventPriorityDraft.ToString();
        var eventRecurrence = _eventRecurrenceDraft.ToString();
        var priority = SelectedTaskPriorityOption?.Value ?? EditPriorityText;
        var preferredTime = SelectedTaskPreferredTimeOption?.Value ?? nameof(PreferredTimeOfDay.Any);
        var maxSplits = SelectedTaskMaxSplitsOption?.Value ?? "3";
        var localIdle = SelectedLocalModelIdleOption?.Value ?? "10";
        ThemeOptions.Clear();
        ThemeOptions.Add(new(nameof(Domain.ShellTheme.Marelian), "MARELIAN"));
        ThemeModeOptions.Clear();
        ThemeModeOptions.Add(new(nameof(Domain.ShellThemeMode.Dark), localization.Text("ThemeModeDark")));
        ThemeModeOptions.Add(new(nameof(Domain.ShellThemeMode.Light), localization.Text("ThemeModeLight")));
        ColorSourceOptions.Clear();
        ColorSourceOptions.Add(new(nameof(Domain.ThemeColorSource.Preset), localization.Text("OptionPreset")));
        ColorSourceOptions.Add(new(nameof(Domain.ThemeColorSource.SystemAccent), localization.Text("OptionSystemAccent")));
        ColorSourceOptions.Add(new(nameof(Domain.ThemeColorSource.Wallpaper), localization.Text("OptionWallpaper")));
        ColorSourceOptions.Add(new(nameof(Domain.ThemeColorSource.Custom), localization.Text("OptionCustom")));
        VisualPaletteOptions.Clear();
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.Vibrant), localization.Text("PaletteVibrant")));
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.Accent), localization.Text("PaletteAccent")));
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.Monochrome), localization.Text("PaletteMonochrome")));
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.Pastel), localization.Text("PalettePastel")));
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.Earth), localization.Text("PaletteEarth")));
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.Neon), localization.Text("PaletteNeon")));
        VisualPaletteOptions.Add(new(nameof(Domain.VisualPalette.ColorBlind), localization.Text("PaletteColorBlind")));
        BuildFontChoices(appFontFamily);
        AssistantProviderOptions.Clear();
        AssistantProviderOptions.Add(new(nameof(Domain.AssistantProvider.Local), localization.Text("OptionProviderLocal")));
        AssistantProviderOptions.Add(new(nameof(Domain.AssistantProvider.OpenAiCompatible), localization.Text("OptionProviderOpenAi")));
        AssistantProviderOptions.Add(new(nameof(Domain.AssistantProvider.Mcp), "MCP"));
        BuildQuietLengthOptions();
        AssistantAccessLevelOptions.Clear();
        AssistantAccessLevelOptions.Add(new(nameof(Domain.AssistantAccessLevel.ConfirmAll), localization.Text("AiAccessConfirmAll")));
        AssistantAccessLevelOptions.Add(new(nameof(Domain.AssistantAccessLevel.AutoSafe), localization.Text("AiAccessAutoSafe")));
        AssistantAccessLevelOptions.Add(new(nameof(Domain.AssistantAccessLevel.FullAccess), localization.Text("AiAccessFull")));
        McpTransportOptions.Clear();
        McpTransportOptions.Add(new("StreamableHttp", localization.Text("OptionTransportHttp")));
        McpTransportOptions.Add(new("Stdio", localization.Text("OptionTransportStdio")));
        BuildCloudChoices();
        LocalModelIdleOptions.Clear();
        LocalModelIdleOptions.Add(new("5", localization.Text("IdleFiveMinutes")));
        LocalModelIdleOptions.Add(new("10", localization.Text("IdleTenMinutes")));
        LocalModelIdleOptions.Add(new("30", localization.Text("IdleThirtyMinutes")));
        LocalModelIdleOptions.Add(new("0", localization.Text("IdleNever")));
        LocalModelOptions.Clear();
        foreach (var descriptor in localModels.Models)
        {
            var status = localModels.GetStatus(new LocalModelSelection(descriptor.Id));
            LocalModelOptions.Add(new(descriptor, localization, option => SelectLocalModel(option))
            {
                IsSelected = descriptor.Id == LocalModelId,
                IsInstalled = status.ModelInstalled,
                IsProjectorInstalled = status.ProjectorInstalled
            });
        }
        // Every category the enum declares, in the order it declares them. Listed one line each,
        // this went out of date the moment a category was added - which is exactly what a
        // chooser must never do, because a category missing from it is a category no one can
        // pick and every event already wearing it becomes unreachable.
        EventCategoryOptions.Clear();
        foreach (var category in CategoryPalette.Categories)
            EventCategoryOptions.Add(new(category.ToString(), localization.Text(EventVisuals.CategoryKey(category)))
            {
                Accent = EventVisuals.AccentBrush(category),
                AccentGlow = EventVisuals.AccentGlowBrush(category)
            });
        EventPriorityOptions.Clear();
        EventPriorityOptions.Add(new(nameof(EventPriority.Normal), localization.Text("EventPriorityNormal")) { Glyph = "○" });
        EventPriorityOptions.Add(new(nameof(EventPriority.High), localization.Text("EventPriorityHigh")) { Glyph = "!" });
        EventPriorityOptions.Add(new(nameof(EventPriority.Critical), localization.Text("EventPriorityCritical")) { Glyph = "‼" });
        EventRecurrenceOptions.Clear();
        EventRecurrenceOptions.Add(new(nameof(EventRecurrence.None), localization.Text("RepeatNever")));
        EventRecurrenceOptions.Add(new(nameof(EventRecurrence.Daily), localization.Text("RepeatDaily")));
        EventRecurrenceOptions.Add(new(nameof(EventRecurrence.Weekdays), localization.Text("RepeatWeekdays")));
        EventRecurrenceOptions.Add(new(nameof(EventRecurrence.Weekly), localization.Text("RepeatWeekly")));
        EventRecurrenceOptions.Add(new(nameof(EventRecurrence.Monthly), localization.Text("RepeatMonthly")));
        EventEditScopeOptions.Clear();
        EventEditScopeOptions.Add(new(nameof(EventSeriesScope.Occurrence), localization.Text("ScopeOccurrence")));
        EventEditScopeOptions.Add(new(nameof(EventSeriesScope.ThisAndFollowing), localization.Text("ScopeFollowing")));
        EventEditScopeOptions.Add(new(nameof(EventSeriesScope.Series), localization.Text("ScopeSeries")));
        TaskPriorityOptions.Clear();
        TaskPriorityOptions.Add(new("25", localization.Text("PriorityLow")) { Glyph = "−" });
        TaskPriorityOptions.Add(new("50", localization.Text("PriorityNormal")) { Glyph = "•" });
        TaskPriorityOptions.Add(new("75", localization.Text("PriorityHigh")) { Glyph = "!" });
        TaskPriorityOptions.Add(new("95", localization.Text("PriorityUrgent")) { Glyph = "‼" });
        TaskPreferredTimeOptions.Clear();
        TaskPreferredTimeOptions.Add(new(nameof(PreferredTimeOfDay.Any), localization.Text("PreferredAny")));
        TaskPreferredTimeOptions.Add(new(nameof(PreferredTimeOfDay.Morning), localization.Text("PreferredMorning")));
        TaskPreferredTimeOptions.Add(new(nameof(PreferredTimeOfDay.Afternoon), localization.Text("PreferredAfternoon")));
        TaskPreferredTimeOptions.Add(new(nameof(PreferredTimeOfDay.Evening), localization.Text("PreferredEvening")));
        TaskMaxSplitsOptions.Clear();
        for (var count = 2; count <= 5; count++) TaskMaxSplitsOptions.Add(new(count.ToString(), count.ToString(localization.CurrentCulture)));
        SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.Value == theme) ?? ThemeOptions[0];
        SelectedThemeModeOption = ThemeModeOptions.FirstOrDefault(option => option.Value == themeMode) ?? ThemeModeOptions[0];
        SelectedColorSourceOption = ColorSourceOptions.First(option => option.Value == colorSource);
        SelectedVisualPaletteOption = VisualPaletteOptions.FirstOrDefault(option => option.Value == visualPalette) ?? VisualPaletteOptions[0];
        SelectedAssistantProviderOption = AssistantProviderOptions.FirstOrDefault(option => option.Value == provider) ?? AssistantProviderOptions[0];
        SelectedAssistantAccessLevelOption = AssistantAccessLevelOptions.FirstOrDefault(option => option.Value == accessLevel) ?? AssistantAccessLevelOptions[0];
        OnPropertyChanged(nameof(AssistantAccessLevelLabel));
        SelectedMcpTransportOption = McpTransportOptions.FirstOrDefault(option => option.Value == transport) ?? McpTransportOptions[0];
        SelectedLocalModelIdleOption = LocalModelIdleOptions.FirstOrDefault(option => option.Value == localIdle) ?? LocalModelIdleOptions[1];
        RefreshLocalTuningText();
        SelectedEventCategoryOption = EventCategoryOptions.FirstOrDefault(option => option.Value == eventCategory) ?? EventCategoryOptions[0];
        SelectedTaskCategoryOption = EventCategoryOptions.FirstOrDefault(option => option.Value == taskCategory) ?? EventCategoryOptions[0];
        SelectedEventPriorityOption = EventPriorityOptions.FirstOrDefault(option => option.Value == eventPriority) ?? EventPriorityOptions[0];
        SelectedEventRecurrenceOption = EventRecurrenceOptions.FirstOrDefault(option => option.Value == eventRecurrence) ?? EventRecurrenceOptions[0];
        SelectedEventEditScopeOption = EventEditScopeOptions.FirstOrDefault();
        SelectedTaskPriorityOption = TaskPriorityOptions.FirstOrDefault(option => option.Value == priority) ?? TaskPriorityOptions[1];
        SelectedTaskPreferredTimeOption = TaskPreferredTimeOptions.FirstOrDefault(option => option.Value == preferredTime) ?? TaskPreferredTimeOptions[0];
        SelectedTaskMaxSplitsOption = TaskMaxSplitsOptions.FirstOrDefault(option => option.Value == maxSplits) ?? TaskMaxSplitsOptions[1];
        RefreshChatModelQuickOptions();
        RefreshAppearanceLabels();
    }

    private void BuildFontChoices(string selectedValue)
    {
        FontOptions.Clear();
        foreach (var font in fontCatalog.GetAvailableFonts())
        {
            var label = font.IsCustom
                ? localization.Format("CustomFontLabel", font.DisplayName)
                : font.Value == FontCatalogService.SystemId
                    ? localization.Text("FontSystem")
                    : font.DisplayName;
            FontOptions.Add(new ChoiceOption(font.Value, label));
        }
        SelectedFontOption = FontOptions.FirstOrDefault(option => option.Value == selectedValue) ?? FontOptions[0];
    }

    private void RefreshLocalizedState()
    {
        Status = localization.Text("StatusReady");
        BuildAnalyticsRangeOptions();
        AssistantState = localization.Text("AssistantPrivacy");
        EventActionText = _editedEvent is null ? localization.Text("AddEvent") : localization.Text("EditEvent");
        TaskActionText = _editedTask is null ? localization.Text("AddTask") : localization.Text("EditTask");
        ComposerPlaceholder = localization.Text("AssistantPlaceholder");
        OnPropertyChanged(nameof(AttachDialogTitle));
        OnPropertyChanged(nameof(ChatExpandTooltip));
        OnPropertyChanged(nameof(TimePickerTitle));
        OnPropertyChanged(nameof(FontDialogTitle));
        OnPropertyChanged(nameof(IsTimePickerPeriodVisible));
        OnPropertyChanged(nameof(WidgetHideDelayText));
        // The only thing a shortcut row carries besides its keys is the sentence saying what it
        // does, so the rows are rebuilt rather than nudged.
        BuildShortcutRows();
        OnPropertyChanged(nameof(ShortcutsHint));
        RefreshFocusModeText();
        ApplyLocalModelStatus(localModels.GetStatus(Selection, CustomModelPath));
        NotifyTimePickerDisplay();
        RefreshClock();
        RefreshDateLabels();
        _ = ReloadLocalizedStateAsync();
    }

    private void RefreshClock()
    {
        CurrentTimeText = ClockText.Format(DateTime.Now.TimeOfDay, Use24HourClock, localization.CurrentCulture);
        RefreshTrackingTimer();
        RefreshPeekEvent();
        TickCloudStatus();
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        // Not while the one question is on screen. Answering it means typing into a row of the
        // day list, and rebuilding that list underneath the answer takes the caret with it.
        // Nor while a step is being typed into a row of it, for the same reason.
        if (_loadingSettings || minute == _lastScheduleRefreshMinute || IsSessionReviewOpen || IsTypingInDay ||
            SelectedDate != DateOnly.FromDateTime(DateTime.Today)) return;
        _lastScheduleRefreshMinute = minute;
        // A quiet spell ends on its own; the shell notices on the same minute boundary it
        // notices everything else on.
        ExpireDoNotDisturb();
        _ = RefreshCurrentEventAsync();
    }

    private async Task RefreshCurrentEventAsync()
    {
        try
        {
            await ReloadDayAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Refreshing current event");
        }
    }

    private void NotifyTimePickerDisplay()
    {
        OnPropertyChanged(nameof(TimePickerHourText));
        OnPropertyChanged(nameof(TimePickerMinuteText));
        OnPropertyChanged(nameof(TimePickerPeriodText));
    }

    private int ParseLocalModelIdleMinutes()
        => int.TryParse(SelectedLocalModelIdleOption?.Value, out var minutes) ? Math.Max(0, minutes) : 10;

    private void LocalModelsOnStatusChanged(object? sender, LocalModelStatusChangedEventArgs args)
        => Dispatcher.UIThread.Post(() => ApplyLocalModelStatus(args.Status));

    private void ApplyLocalModelStatus(LocalModelRuntimeStatus status)
    {
        if (!LocalModelCatalog.IsCustom(LocalModelId))
        {
            var canonicalId = LocalModelCatalog.Find(LocalModelId).Id;
            if (LocalModelId != canonicalId) LocalModelId = canonicalId;
        }

        foreach (var option in LocalModelOptions)
        {
            var optionStatus = localModels.GetStatus(new LocalModelSelection(option.Id));
            option.IsSelected = option.Id == LocalModelId;
            option.IsInstalled = optionStatus.ModelInstalled;
            option.IsProjectorInstalled = optionStatus.ProjectorInstalled;
        }

        var selected = localModels.GetStatus(Selection, CustomModelPath);
        var activeStatus = localModels.Status;

        // A model that has just arrived on disk can be asked what it really needs, which is what
        // the tuner has been estimating until now.
        var arrived = selected.ModelInstalled && !IsSelectedLocalModelInstalled;
        IsSelectedLocalModelInstalled = selected.ModelInstalled;
        if (arrived) RetuneLocalModel();
        IsSelectedProjectorInstalled = selected.ProjectorInstalled;
        IsLocalModelRunning = activeStatus.State == LocalModelRuntimeState.Running;
        IsLocalModelStarting = activeStatus.State == LocalModelRuntimeState.Starting;
        IsLocalModelTransferring = activeStatus.State is LocalModelRuntimeState.DownloadingEngine
            or LocalModelRuntimeState.ExtractingEngine or LocalModelRuntimeState.DownloadingModel
            or LocalModelRuntimeState.DownloadingProjector;
        LocalModelProgress = activeStatus.Progress;
        LocalModelStateText = activeStatus.State switch
        {
            LocalModelRuntimeState.DownloadingEngine => localization.Text("DownloadingEngine"),
            LocalModelRuntimeState.ExtractingEngine => localization.Text("ExtractingEngine"),
            LocalModelRuntimeState.DownloadingModel => localization.Text("DownloadingModel"),
            LocalModelRuntimeState.DownloadingProjector => localization.Text("DownloadingVision"),
            LocalModelRuntimeState.Starting => localization.Text("StartingLocalModel"),
            LocalModelRuntimeState.Running => localization.Text("LocalModelRunning"),
            LocalModelRuntimeState.Sleeping => localization.Text("LocalModelSleeping"),
            LocalModelRuntimeState.Error => activeStatus.Error ?? localization.Text("LocalModelError"),
            _ => localization.Text(selected.ModelInstalled ? "LocalModelStopped" : "LocalModelNotInstalled")
        };
        LocalModelTransferText = activeStatus.TotalBytes is > 0
            ? $"{FormatBytes(activeStatus.DownloadedBytes)} / {FormatBytes(activeStatus.TotalBytes.Value)}"
            : activeStatus.DownloadedBytes > 0 ? FormatBytes(activeStatus.DownloadedBytes) : string.Empty;
        OnPropertyChanged(nameof(NeedsLocalModelDownload));
        OnPropertyChanged(nameof(NeedsProjectorDownload));
        OnPropertyChanged(nameof(SelectedModelSupportsVision));
        OnPropertyChanged(nameof(IsVisionActive));
        OnPropertyChanged(nameof(ShowProjectorPicker));
        OnPropertyChanged(nameof(VisionRequirementText));
        OnPropertyChanged(nameof(CanDownloadLocalModel));
        OnPropertyChanged(nameof(CanStartLocalModel));
        OnPropertyChanged(nameof(CanStopLocalModel));
        OnPropertyChanged(nameof(CanToggleLocalModelExecution));
        OnPropertyChanged(nameof(CanDeleteLocalModel));
        OnPropertyChanged(nameof(LocalModelDownloadActionText));
        OnPropertyChanged(nameof(LocalModelExecutionTooltip));
        OnPropertyChanged(nameof(ShowLocalModelResourceMonitor));
        OnPropertyChanged(nameof(RunningLocalModelDisplayName));
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        OnPropertyChanged(nameof(LocalProviderMenuSubtitle));
        OnPropertyChanged(nameof(SelectedQuantLabel));
        OnPropertyChanged(nameof(SelectedLocalModelDisplayName));
        OnPropertyChanged(nameof(ShowCatalogModelActions));
        UpdateResourceMetrics();
        RefreshChatModelQuickOptions();
    }

    private void UpdateResourceMetrics()
    {
        if (!IsLocalModelRunning && localModels.Status.State != LocalModelRuntimeState.Running || !IsLocalProviderActive)
        {
            OnPropertyChanged(nameof(ShowLocalModelResourceMonitor));
            return;
        }
        var metrics = localModels.GetResourceMetrics();
        if (metrics is null)
        {
            OnPropertyChanged(nameof(ShowLocalModelResourceMonitor));
            return;
        }
        LocalModelMemoryUsageText = FormatBytes(metrics.MemoryBytes);
        LocalModelCpuUsageText = $"{metrics.CpuPercent:0.#}%";
        LocalModelCpuFraction = Math.Clamp(metrics.CpuPercent / 100.0, 0, 1);
        // A number of bytes says how much; it does not say whether that is a lot. Given the
        // memory in the machine it can say both.
        LocalModelMemoryScaleText = metrics.TotalMemoryBytes > 0
            ? localization.Format("ResourceOfTotal", FormatBytes(metrics.TotalMemoryBytes))
            : string.Empty;
        LocalModelMemoryFraction = metrics.TotalMemoryBytes > 0
            ? Math.Clamp((double)metrics.MemoryBytes / metrics.TotalMemoryBytes, 0, 1)
            : 0;
        // What the engine is actually doing, not what it was asked to do: a card that could not
        // take the model leaves the layer count at zero and the capsule says CPU.
        IsLocalModelOnGpu = metrics.IsGpuAccelerated;
        LocalModelAcceleratorText = localization.Text(metrics.IsGpuAccelerated ? "GpuLabel" : "CpuOnlyLabel");
        // Three states, and they are not the same thing: the model is not on the card, the
        // machine will not say how busy the card is, or here is how busy it is.
        LocalModelGpuUsageText = !metrics.IsGpuAccelerated
            ? localization.Text("ResourceNotInUse")
            : metrics.GpuPercent is { } gpu ? $"{gpu:0.#}%" : localization.Text("ResourceUnavailable");
        LocalModelGpuFraction = metrics.GpuPercent is { } share ? Math.Clamp(share / 100.0, 0, 1) : 0;
        // Video memory is the fourth column rather than a row of its own under the other three:
        // it is the same kind of reading as the rest and belongs beside them. On a model that
        // is on the processor it says so, exactly as the card's own column does.
        LocalModelVideoMemoryText = !metrics.IsGpuAccelerated
            ? localization.Text("ResourceNotInUse")
            : metrics.VideoMemoryBytes > 0
                ? FormatBytes(metrics.VideoMemoryBytes)
                : localization.Text("ResourceUnavailable");
        LocalModelVideoMemoryFraction = metrics.VideoMemoryTotalBytes > 0
            ? Math.Clamp((double)metrics.VideoMemoryBytes / metrics.VideoMemoryTotalBytes, 0, 1)
            : 0;
        LocalModelVideoMemoryScaleText = metrics.VideoMemoryTotalBytes > 0
            ? localization.Format("ResourceOfTotal", FormatBytes(metrics.VideoMemoryTotalBytes))
            : string.Empty;
        // The badge's tooltip names the card. The layer count is only added when the engine
        // actually printed one — current builds do not, and "0 of 0 layers" is worse than
        // saying nothing about them.
        LocalModelAcceleratorDetail = !metrics.IsGpuAccelerated
            ? localization.Text("CpuOnlyDetail")
            : metrics.TotalLayers > 0
                ? localization.Format("GpuOffloadDetail",
                    metrics.Accelerator ?? localization.Text("GpuLabel"), metrics.GpuLayers,
                    Math.Max(metrics.GpuLayers, metrics.TotalLayers))
                : metrics.Accelerator ?? localization.Text("GpuLabel");
        LocalModelUptimeText = metrics.Uptime.TotalHours >= 1
            ? metrics.Uptime.ToString(@"hh\:mm\:ss")
            : metrics.Uptime.ToString(@"mm\:ss");
        LocalModelMonitorTag = metrics.ModelName;
        OnPropertyChanged(nameof(ShowLocalModelResourceMonitor));
        OnPropertyChanged(nameof(RunningLocalModelDisplayName));
    }

    public void RefreshChatModelQuickOptions()
    {
        LocalModelQuickOptions.Clear();
        foreach (var desc in localModels.Models)
        {
            var status = localModels.GetStatus(new LocalModelSelection(desc.Id));
            if (!status.ModelInstalled) continue;

            var quant = desc.Quant;
            var isSel = AssistantProvider == nameof(Domain.AssistantProvider.Local) && LocalModelId == desc.Id;
            var subtitle = $"{desc.ParameterLabel} · {quant.Tag} · {quant.ApproximateSizeLabel}";
            var badge = desc.IsRecommended ? localization.Text("Recommended") : desc.ParameterLabel;
            LocalModelQuickOptions.Add(new ChatModelQuickOptionViewModel(desc.Id, nameof(Domain.AssistantProvider.Local),
                desc.DisplayName, subtitle, badge, SelectChatQuickModelAsync)
            {
                IsSelected = isSel,
                IsInstalled = true
            });
        }

        if (HasCustomModel)
            LocalModelQuickOptions.Add(new ChatModelQuickOptionViewModel(LocalModelCatalog.CustomModelId,
                nameof(Domain.AssistantProvider.Local), CustomModelFileName, localization.Text("CustomModelBadge"),
                localization.Text("CustomModelBadge"), SelectChatQuickModelAsync)
            {
                IsSelected = IsCustomModelSelected,
                IsInstalled = true
            });

        ExternalModelQuickOptions.Clear();
        ExternalModelQuickOptions.Add(new ChatModelQuickOptionViewModel("openai", nameof(Domain.AssistantProvider.OpenAiCompatible),
            localization.Text("OptionProviderOpenAi"), string.IsNullOrWhiteSpace(AssistantModel) ? "http / https" : AssistantModel, "API",
            SelectChatQuickModelAsync)
        {
            IsSelected = AssistantProvider == nameof(Domain.AssistantProvider.OpenAiCompatible) || AssistantProvider == nameof(Domain.AssistantProvider.Ollama)
        });

        ExternalModelQuickOptions.Add(new ChatModelQuickOptionViewModel("mcp", nameof(Domain.AssistantProvider.Mcp),
            "MCP", McpTransport == "Stdio" ? "Stdio tool" : "Streamable HTTP", "MCP",
            SelectChatQuickModelAsync)
        {
            IsSelected = AssistantProvider == nameof(Domain.AssistantProvider.Mcp)
        });

        OnPropertyChanged(nameof(HasInstalledLocalModels));
        OnPropertyChanged(nameof(LocalProviderMenuSubtitle));
        OnPropertyChanged(nameof(ActiveModelDisplayName));
        OnPropertyChanged(nameof(CanToggleLocalModelExecution));
    }

    private Task SelectChatQuickModelAsync(ChatModelQuickOptionViewModel option)
    {
        RequestSwitchModel(option.Id, option.Provider);
        return Task.CompletedTask;
    }

    private static string FormatBytes(long bytes) => BinarySizeFormatter.Format(bytes);

    private void ClearEventValidation()
    {
        if (EventValidationMessage.Length > 0) EventValidationMessage = string.Empty;
    }

    public string FormatClockTime(TimeSpan? value)
        => value is null
            ? "—"
            : ClockText.Format(value.Value, Use24HourClock, localization.CurrentCulture);

    /// <summary>The grid a drag is rounding to, shown on the selection while Shift is held.</summary>
    public string FormatSnapStep(int minutes) => localization.Format("SnapStep", minutes);

    private async Task ReloadLocalizedStateAsync()
    {
        try { await ReloadAsync(preserveStatus: true); await RefreshCalendarAsync(); }
        catch (Exception exception) { crashReporter.Record(exception, "Refreshing localization"); }
    }

    private static TaskItem FromSuggestion(AssistantTaskSuggestion suggestion) => new()
    {
        Title = suggestion.Title.Trim(), Status = DomainTaskStatus.Ready, Priority = Math.Clamp(suggestion.Priority, 0, 100),
        EstimatedDurationMin = NormalizeMinutes(suggestion.DurationMinutes), RemainingDurationMin = NormalizeMinutes(suggestion.DurationMinutes),
        DeadlineUtc = suggestion.DeadlineLocal?.ToUniversalTime(), PreferredTimeOfDay = suggestion.PreferredTime,
        Splittable = suggestion.DurationMinutes >= 60, MinBlockMin = Math.Min(30, NormalizeMinutes(suggestion.DurationMinutes)), MaxSplits = 3
    };

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true;
        try { await action(); await ReloadAsync(preserveStatus: true); }
        catch (Exception ex) { crashReporter.Record(ex, "View model operation"); Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly date)
        => LocalDateRange.Utc(date);
    private static DateTimeOffset ToLocalOffset(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
    private static DateTimeOffset RoundUp(DateTimeOffset value, int minutes)
    {
        var ticks = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTimeOffset(((value.Ticks + ticks - 1) / ticks) * ticks, value.Offset);
    }
    private static int NormalizeMinutes(int value) => Math.Max(1, value);
    private static T Parse<T>(string value, T fallback) where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
    private static string NormalizeColor(string value, string fallback)
    {
        try { _ = Avalonia.Media.Color.Parse(value); return value.StartsWith('#') ? value : $"#{value}"; }
        catch { return fallback; }
    }
}


internal enum CalendarSelectionPurpose
{
    Schedule,
    Event,
    EventCopy,
    DayCopy,
    TaskDeadline,
    TaskEarliestStart,
    TaskBlock,
    /// <summary>The day a picked set of events is being moved onto.</summary>
    MoveTarget,
    /// <summary>The day a whole day's schedule is being shifted onto.</summary>
    DayShiftTarget,
    /// <summary>The day the next step written into a task's project will be planned for.</summary>
    ProjectDraftDay,
    /// <summary>The day an existing project step is moved to.</summary>
    ProjectStepDay
}

internal enum TimeSelectionPurpose
{
    EventStart,
    EventEnd,
    TaskBlockStart,
    TaskBlockEnd,
    DayShift,
    TaskDuration,
    /// <summary>How far a picked set of events is being moved.</summary>
    MoveAmount,
    /// <summary>How long the next step written into a task's project will take.</summary>
    ProjectDraftDuration,
    /// <summary>How long an existing step of a task's project takes.</summary>
    ProjectStepDuration,
    /// <summary>How long the next step written into an event's checklist will take.</summary>
    SittingDraftDuration,
    /// <summary>How long an existing step of an event's checklist takes.</summary>
    SittingStepDuration,
    /// <summary>How long a step written into the event or task form takes.</summary>
    FormDraftDuration
}

internal enum TaskBlockPendingAction
{
    None,
    Save,
    Complete,
    Unschedule,
    NotToday
}
