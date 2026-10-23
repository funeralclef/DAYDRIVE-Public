using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// A throwaway SQLite database plus the full assistant object graph, so tests exercise the
/// real repositories, the real toolbox and the real chat loop instead of mocks.
/// </summary>
public sealed class AssistantTestHarness : IAsyncDisposable
{
    private readonly string _directory;

    private AssistantTestHarness(string directory, SqliteStore store, ChatStore chat, LocalSecretProtector protector,
        IDbContextFactory<AppDbContext> factory)
    {
        _directory = directory;
        Directory = directory;
        DatabasePath = Path.Combine(directory, "scheduler.db");
        ContextFactory = factory;
        Store = store;
        Chat = chat;
        Protector = protector;
        Executor = new ScheduleActionExecutor(store, store, store);
        Outcomes = new EventOutcomeStore(factory);
        FollowUps = new EventFollowUpStore(factory);
        Projects = new ProjectStore(factory);
        Toolbox = new ScheduleToolbox(store, store, store, store, Outcomes, chat, store, Executor, Projects);
    }

    /// <summary>Temporary root for this test, useful as a stand-in cloud folder.</summary>
    public string Directory { get; }
    public string DatabasePath { get; }
    public IDbContextFactory<AppDbContext> ContextFactory { get; }
    public SqliteStore Store { get; }
    public ChatStore Chat { get; }
    public LocalSecretProtector Protector { get; }
    public ScheduleActionExecutor Executor { get; }
    public ScheduleToolbox Toolbox { get; }
    public EventOutcomeStore Outcomes { get; }
    public EventFollowUpStore FollowUps { get; }
    public ProjectStore Projects { get; }
    public McpToolGateway? Mcp { get; private set; }

    public static async Task<AssistantTestHarness> CreateAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"daydrive-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "scheduler.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False").Options;
        var factory = new Factory(options);
        await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
        return new AssistantTestHarness(directory, new SqliteStore(factory), new ChatStore(factory),
            new LocalSecretProtector(directory), factory);
    }

    public McpToolGateway UseMcp(Func<AppSettings, ModelContextProtocol.Client.IClientTransport> transportFactory)
        => Mcp = new McpToolGateway(Store, Protector, transportFactory);

    public AssistantService CreateAssistant(IAssistantToolbox? toolbox = null, McpToolGateway? mcp = null)
        => new(Store, Store, Store, new NaturalLanguageTaskParser(), new StubLocalModelRuntime(), Protector,
            toolbox ?? Toolbox, mcp ?? Mcp ?? new McpToolGateway(Store, Protector), Chat, Chat);

    public async Task<AppSettings> ConfigureAsync(Action<AppSettings> configure, CancellationToken cancellationToken)
    {
        var settings = await Store.GetAppSettingsAsync(cancellationToken);
        configure(settings);
        await Store.UpdateAppSettingsAsync(settings, cancellationToken);
        return await Store.GetAppSettingsAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Mcp is not null) await Mcp.DisposeAsync();
        try { System.IO.Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* the SQLite handle can outlive the test on Windows */ }
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}

/// <summary>A local runtime that never starts a real llama-server.</summary>
public sealed class StubLocalModelRuntime : ILocalModelRuntime
{
    public IReadOnlyList<LocalModelDescriptor> Models => LocalModelCatalog.Models;
    public LocalModelRuntimeStatus Status => GetStatus(new LocalModelSelection(LocalModelCatalog.DefaultModelId));
    public event EventHandler<LocalModelStatusChangedEventArgs>? StatusChanged { add { } remove { } }
    public LocalModelRuntimeStatus GetStatus(LocalModelSelection selection, string? customModelPath = null)
        => new(selection.ModelId, LocalModelRuntimeState.Stopped, false, false, false);
    public string? ModelFilePath(LocalModelSelection selection) => null;
    public HardwareProfile ReadHardware() => HardwareProfile.Unknown;
    public long RetiredDownloadBytes() => 0;
    public Task<long> DeleteRetiredDownloadsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public LocalModelResourceMetrics? GetResourceMetrics() => null;
    public Task RefreshAsync(LocalModelSelection selection, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DownloadAsync(LocalModelSelection selection, bool includeProjector, bool useGpu = true,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(LocalModelSelection selection, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<Uri> EnsureReadyAsync(LocalModelSelection selection, LocalRuntimeOptions options, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException();
    public Task StopAsync(bool sleeping = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void NotifyActivity(int idleMinutes) { }
    public void Dispose() { }
}
