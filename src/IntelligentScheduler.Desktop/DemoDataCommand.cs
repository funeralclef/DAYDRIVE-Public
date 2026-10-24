using System.Globalization;
using IntelligentScheduler.Application;
using IntelligentScheduler.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// Handles <c>--seed-demo</c>. It writes real rows into whichever data directory is active,
/// so it refuses to touch a database that already holds events unless <c>--seed-force</c> is
/// passed. Pair it with <c>--data-dir</c> to keep a real calendar untouched.
/// </summary>
internal static class DemoDataCommand
{
    public static async Task Run(IServiceProvider services, string argument, bool force)
    {
        var events = services.GetRequiredService<ICalendarRepository>();
        var existing = await events.GetAllAsync();
        if (existing.Count > 0 && !force)
        {
            Console.Error.WriteLine(
                $"This database already contains {existing.Count} event(s). Refusing to add demo data on top of it.\n" +
                "Use a throwaway database:\n" +
                "  --seed-demo --data-dir=C:\\temp\\daydrive-demo\n" +
                "or pass --seed-force to add demo data to the current one anyway.");
            Environment.ExitCode = 1;
            return;
        }

        var days = ParseDays(argument);
        var summary = await DemoDataSeeder.SeedAsync(
            events,
            services.GetRequiredService<ITaskRepository>(),
            services.GetRequiredService<IBlockRepository>(),
            services.GetRequiredService<ITaskExecutionRepository>(),
            new DemoDataOptions(Days: days),
            outcomes: services.GetRequiredService<IEventOutcomeRepository>(),
            projects: services.GetRequiredService<IProjectRepository>());

        Console.WriteLine($"Seeded {days} days of demo history: {summary}");
        Console.WriteLine("Start the app against the same --data-dir and open the analytics tab.");
    }

    /// <summary>Accepts both <c>--seed-demo</c> and <c>--seed-demo=180</c>.</summary>
    private static int ParseDays(string argument)
    {
        var separator = argument.IndexOf('=');
        if (separator < 0) return 120;
        return int.TryParse(argument[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 7, 400)
            : 120;
    }
}
