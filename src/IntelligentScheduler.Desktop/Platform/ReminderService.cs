using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Localization;

namespace IntelligentScheduler.Desktop.Platform;

public sealed class ReminderService(
    ICalendarRepository calendar,
    ISettingsRepository settings,
    INotificationService notifications,
    LocalizationService localization) : IDisposable
{
    private readonly HashSet<string> _notified = [];
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(cancellationToken);
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken))
                await CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!(await settings.GetAppSettingsAsync(cancellationToken)).NotificationsEnabled) return;

            var now = DateTimeOffset.UtcNow;
            var reminderEnd = now.AddMinutes(10);
            var events = await calendar.GetRangeAsync(now, reminderEnd, cancellationToken);
            foreach (var calendarEvent in events)
            {
                await NotifyStartAsync(calendarEvent.Id, calendarEvent.Title, calendarEvent.StartUtc, now, reminderEnd, cancellationToken);
                if (calendarEvent.StartUtc <= now)
                    await NotifyEndAsync(calendarEvent.Id, calendarEvent.Title, calendarEvent.EndUtc, now, reminderEnd, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task NotifyStartAsync(Guid id, string title, DateTimeOffset startsAt, DateTimeOffset now,
        DateTimeOffset reminderEnd, CancellationToken cancellationToken)
    {
        if (startsAt <= now || startsAt > reminderEnd) return;
        var key = $"{id}:start:{startsAt.UtcTicks}";
        if (!_notified.Add(key)) return;
        var minutes = Math.Max(1, (int)Math.Ceiling((startsAt - now).TotalMinutes));
        await notifications.ShowAsync(
            localization.Text("StartingSoon"),
            localization.Format("StartsInMinutes", title, minutes),
            id,
            cancellationToken);
    }

    private async Task NotifyEndAsync(Guid id, string title, DateTimeOffset endsAt, DateTimeOffset now,
        DateTimeOffset reminderEnd, CancellationToken cancellationToken)
    {
        if (endsAt <= now || endsAt > reminderEnd) return;
        var key = $"{id}:end:{endsAt.UtcTicks}";
        if (!_notified.Add(key)) return;
        var minutes = Math.Max(1, (int)Math.Ceiling((endsAt - now).TotalMinutes));
        await notifications.ShowAsync(
            localization.Text("EndingSoon"),
            localization.Format("EndsInMinutes", title, minutes),
            id,
            cancellationToken);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
    }
}
