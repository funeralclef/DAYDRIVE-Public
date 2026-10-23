namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>The moods the sync card can be in, spelled as the classes its dot is styled by.</summary>
public static class CloudStatusKinds
{
    public const string Ok = "ok";
    public const string Pending = "pending";
    public const string Busy = "busy";
    public const string Error = "error";
    public const string Attention = "attention";
}

/// <summary>One line of the sync history as the settings page shows it.</summary>
public sealed class CloudJournalItemViewModel(string when, string text, string trigger, string kind)
{
    public string When { get; } = when;
    public string Text { get; } = text;
    /// <summary>Who asked: the user, the timer, the launch or the exit.</summary>
    public string Trigger { get; } = trigger;
    public bool IsOk => kind == CloudStatusKinds.Ok;
    public bool IsError => kind == CloudStatusKinds.Error;
    public bool IsAttention => kind == CloudStatusKinds.Attention;
}
