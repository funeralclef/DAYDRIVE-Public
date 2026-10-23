using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.Headless;
using Avalonia.Dialogs;
using IntelligentScheduler.Infrastructure;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentScheduler.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WaitForPreviousInstance(args);
        if (TryRunNonInteractiveCommand(args)) return;
        if (!LinuxDesktopControl.StartOrForward(args)) return;

        var rendersQaSnapshots = args.Any(argument =>
            argument.Equals("--render-qa-snapshots", StringComparison.OrdinalIgnoreCase));
        var exitCode = BuildAvaloniaApp(rendersQaSnapshots).StartWithClassicDesktopLifetime(args);
        LinuxDesktopControl.Current?.Dispose();
        // The message loop is over, so nothing is left that the user can see or act on. A single
        // lingering foreground thread — a tray icon's, a model host's, a stray timer's — would
        // keep the process alive with no window left to close, and a shutting-down Windows would
        // sit and wait on it. Leaving is explicit rather than a consequence of everything having
        // finished on its own.
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// A restart the running copy asked for: it passes its process id on the way out. Waiting
    /// for it here, before anything touches the data folder, is what lets a staged cloud download
    /// be swapped in — the database cannot be replaced while the old process still holds it.
    /// </summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        const string prefix = "--wait-for-exit=";
        var argument = args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (argument is null || !int.TryParse(argument[prefix.Length..], out var processId)) return;
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(processId);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone, which is the point.
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Commands that only work with the local database must not initialise a rendering backend.
    /// In particular, seeding demo data is useful in CI and over SSH, where macOS has no display
    /// link for Avalonia to attach to yet.
    /// </summary>
    private static bool TryRunNonInteractiveCommand(string[] args)
    {
        var seedArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--seed-demo", StringComparison.OrdinalIgnoreCase));
        if (seedArgument is null) return false;

        using var services = App.BuildServices();
        services.GetRequiredService<DatabaseInitializer>().InitializeAsync().GetAwaiter().GetResult();
        DemoDataCommand.Run(services, seedArgument,
                args.Any(argument => argument.Equals("--seed-force", StringComparison.OrdinalIgnoreCase)))
            .GetAwaiter().GetResult();
        return true;
    }

    public static AppBuilder BuildAvaloniaApp(bool headless = false)
    {
        if (!headless) LinuxLayerShell.Initialize();
        var builder = AppBuilder.Configure<App>().WithInterFont().LogToTrace()
            .With(new X11PlatformOptions
            {
                OverlayPopups = ShellEnvironment.Mode == DesktopShellMode.Windowed || LinuxLayerShell.IsSupported,
                UseGLibMainLoop = LinuxLayerShell.UsesGtk,
                WmClass = "DAYDRIVE"
            });
        if (!headless && OperatingSystem.IsLinux() && LinuxLayerShell.IsSupported && ShellEnvironment.Mode == DesktopShellMode.Edge)
            builder = builder.UseManagedSystemDialogs();
        return headless
            ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            })
            : builder.UsePlatformDetect().AfterSetup(_ => LinuxLayerShell.Install());
    }
}
