using System.Text.RegularExpressions;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

public sealed partial class NaturalLanguageTaskParser : IAIService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<TaskDraft> ParseTaskAsync(string input, UserPlanningContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Describe the task first.", nameof(input));

        var source = input.Trim();
        var assumptions = new List<string>();
        var duration = ParseDuration(source, context.DefaultDurationMin, assumptions, out var durationConfidence);
        var priority = ParsePriority(source, out var priorityConfidence);
        var preferred = ParsePreferredTime(source);
        var deadline = ParseDeadline(source, context.NowLocal, preferred, assumptions, out var deadlineConfidence);
        var title = CleanTitle(source);
        if (string.IsNullOrWhiteSpace(title)) title = "New task";

        var minBlock = duration >= 90 ? 45 : Math.Min(30, duration);
        var draft = new TaskDraft(
            title,
            duration,
            deadline,
            priority,
            duration >= 60,
            minBlock,
            preferred,
            new TaskDraftConfidence(durationConfidence, deadlineConfidence, priorityConfidence),
            assumptions);
        return Task.FromResult(draft);
    }

    private static int ParseDuration(string input, int fallback, List<string> assumptions, out double confidence)
    {
        var hours = HoursRegex().Match(input);
        var minutes = MinutesRegex().Match(input);
        if (hours.Success)
        {
            var value = double.Parse(hours.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var total = (int)Math.Round(value * 60);
            if (minutes.Success) total += int.Parse(minutes.Groups[1].Value);
            confidence = 0.96;
            return Math.Clamp(total, 1, 720);
        }
        if (minutes.Success)
        {
            confidence = 0.96;
            return Math.Clamp(int.Parse(minutes.Groups[1].Value), 1, 720);
        }
        confidence = 0.35;
        assumptions.Add($"Duration assumed to be {fallback} minutes.");
        return Math.Clamp(fallback, 1, 720);
    }

    private static int ParsePriority(string input, out double confidence)
    {
        if (HighPriorityRegex().IsMatch(input)) { confidence = 0.9; return 85; }
        if (LowPriorityRegex().IsMatch(input)) { confidence = 0.86; return 25; }
        confidence = 0.45;
        return 50;
    }

    private static PreferredTimeOfDay ParsePreferredTime(string input)
    {
        if (MorningRegex().IsMatch(input)) return PreferredTimeOfDay.Morning;
        if (AfternoonRegex().IsMatch(input)) return PreferredTimeOfDay.Afternoon;
        if (EveningRegex().IsMatch(input)) return PreferredTimeOfDay.Evening;
        return PreferredTimeOfDay.Any;
    }

    private static DateTimeOffset? ParseDeadline(string input, DateTimeOffset now, PreferredTimeOfDay preferred,
        List<string> assumptions, out double confidence)
    {
        DateTime date;
        if (TomorrowRegex().IsMatch(input)) date = now.LocalDateTime.Date.AddDays(1);
        else if (TodayRegex().IsMatch(input)) date = now.LocalDateTime.Date;
        else
        {
            var numeric = DateRegex().Match(input);
            if (!numeric.Success)
            {
                confidence = 0.2;
                return null;
            }
            var day = int.Parse(numeric.Groups[1].Value);
            var month = int.Parse(numeric.Groups[2].Value);
            var year = numeric.Groups[3].Success ? int.Parse(numeric.Groups[3].Value) : now.Year;
            try { date = new DateTime(year, month, day); }
            catch { confidence = 0.1; return null; }
        }

        var timeMatch = TimeRegex().Match(input);
        var hour = timeMatch.Success ? int.Parse(timeMatch.Groups[1].Value) : preferred switch
        {
            PreferredTimeOfDay.Morning => 12,
            PreferredTimeOfDay.Afternoon => 17,
            PreferredTimeOfDay.Evening => 22,
            _ => 22
        };
        var minute = timeMatch.Success && timeMatch.Groups[2].Success ? int.Parse(timeMatch.Groups[2].Value) : 0;
        var local = DateTime.SpecifyKind(date.AddHours(Math.Clamp(hour, 0, 23)).AddMinutes(Math.Clamp(minute, 0, 59)), DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        confidence = timeMatch.Success ? 0.94 : 0.78;
        if (!timeMatch.Success) assumptions.Add($"Deadline time assumed to be {hour:00}:00.");
        return new DateTimeOffset(local, offset);
    }

    private static string CleanTitle(string input)
    {
        var title = input;
        foreach (var regex in MetadataRegexes)
            title = regex.Replace(title, " ");
        title = SeparatorRegex().Replace(title, " ").Trim(' ', ',', '.', ';', '-', '—');
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        return char.ToUpperInvariant(title[0]) + title[1..];
    }

    private static readonly Regex[] MetadataRegexes =
    [
        HoursRegex(), MinutesRegex(), TomorrowRegex(), TodayRegex(), DateRegex(), TimeRegex(),
        HighPriorityRegex(), LowPriorityRegex(), MorningRegex(), AfternoonRegex(), EveningRegex(),
        DeadlineWordsRegex()
    ];

    [GeneratedRegex(@"(?<!\d)(\d+(?:[\.,]\d+)?)\s*(?:h|hr|hrs|hour|hours|ч|час|часа|часов)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoursRegex();
    [GeneratedRegex(@"(?<!\d)(\d{1,3})\s*(?:m|min|mins|minute|minutes|мин|минута|минуты|минут)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesRegex();
    [GeneratedRegex(@"\b(?:tomorrow|завтра)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TomorrowRegex();
    [GeneratedRegex(@"\b(?:today|сегодня)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TodayRegex();
    [GeneratedRegex(@"\b(\d{1,2})[\./](\d{1,2})(?:[\./](\d{2,4}))?\b")]
    private static partial Regex DateRegex();
    [GeneratedRegex(@"\b(?:at|в)?\s*([01]?\d|2[0-3])[:.]([0-5]\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();
    [GeneratedRegex(@"\b(?:urgent|important|high priority|важно|срочно|высокий приоритет)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HighPriorityRegex();
    [GeneratedRegex(@"\b(?:low priority|not urgent|не срочно|низкий приоритет)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowPriorityRegex();
    [GeneratedRegex(@"\b(?:morning|утром|утро)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MorningRegex();
    [GeneratedRegex(@"\b(?:afternoon|днем|днём|день)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AfternoonRegex();
    [GeneratedRegex(@"\b(?:evening|вечером|вечер)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EveningRegex();
    [GeneratedRegex(@"\b(?:by|due|deadline|до|дедлайн)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeadlineWordsRegex();
    [GeneratedRegex(@"\s{2,}|\s*[,;]+\s*")]
    private static partial Regex SeparatorRegex();
}
