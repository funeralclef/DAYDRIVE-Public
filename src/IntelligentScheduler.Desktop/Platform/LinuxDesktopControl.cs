using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// One Linux instance per data directory. Compositor key bindings send the same shell actions
/// as Windows hotkeys, without opening another database or claiming Wayland's keyboard.
/// The pipe only accepts connections from this user; messages contain arguments, never code.
/// </summary>
internal sealed class LinuxDesktopControl : IDisposable
{
    public static LinuxDesktopControl? Current { get; private set; }
    private readonly FileStream _lock;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string[]> _pending = new();
    private Action<string[]>? _receive;
    private Action? _schedule;
    private NamedPipeServerStream? _pipe;
    private int _disposed;

    private LinuxDesktopControl(FileStream file, string name)
    {
        _lock = file;
        _name = name;
        Current = this;
        _ = Task.Run(ListenAsync);
    }

    /// <returns>True if a new application should start, false after forwarding to its owner.</returns>
    public static bool StartOrForward(string[] args)
    {
        if (!OperatingSystem.IsLinux() || args.Any(a => a.Equals("--render-qa-snapshots", StringComparison.OrdinalIgnoreCase))) return true;
        _ = RequestedAction(args); // Fail early on a misspelled action, including cold starts.
        var directory = DataDirectoryResolver.Resolve(args, Environment.GetEnvironmentVariable("INTELLIGENT_SCHEDULER_DATA_DIR"),
            AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(directory);
        var name = "daydrive-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory))))[..32];
        FileStream file;
        try { file = new FileStream(Path.Combine(directory, ".desktop-instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(10000);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(args));
            using var reader = new StreamReader(client, leaveOpen: true);
            if (reader.ReadLine() != "OK") throw new IOException("DAYDRIVE did not accept the launch request.");
            return false;
        }
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) { file.Dispose(); return false; }
        var owner = new LinuxDesktopControl(file, name);
        if (RequestedAction(args) is not null) owner._pending.Enqueue(args);
        return true;
    }

    public static ShellShortcutAction? RequestedAction(IEnumerable<string> args)
    {
        var option = args.FirstOrDefault(a => a.StartsWith("--action=", StringComparison.OrdinalIgnoreCase));
        if (option is null) return null;
        var value = option["--action=".Length..].Replace("-", "");
        if (Enum.TryParse<ShellShortcutAction>(value, true, out var action) && Enum.IsDefined(action)) return action;
        throw new ArgumentException("Unknown DAYDRIVE action: " + option);
    }

    public void Attach(Action<string[]> receive)
    {
        _receive = receive;
        // Do not initialize Avalonia's dispatcher from the pipe's worker thread while the
        // first instance is still starting GTK/X11. Attach is called after UI setup.
        var dispatcher = Dispatcher.UIThread;
        Volatile.Write(ref _schedule, () => dispatcher.Post(Drain));
        Drain();
    }

    private void Drain()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_receive is not { } receive) return;
        while (_pending.TryDequeue(out var args)) receive(args);
    }

    private async Task ListenAsync()
    {
        try
        {
            using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _pipe = pipe;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    var json = await reader.ReadLineAsync(_stop.Token);
                    if (json is null || json.Length > 16384) continue;
                    var args = JsonSerializer.Deserialize<string[]>(json);
                    if (args is null || args.Any(a => a is null)) continue;
                    _ = RequestedAction(args);
                    _pending.Enqueue(args);
                    Volatile.Read(ref _schedule)?.Invoke();
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync("OK");
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or JsonException or ArgumentException)
                {
                    if (_stop.IsCancellationRequested) return;
                }
                finally
                {
                    if (!_stop.IsCancellationRequested && pipe.IsConnected) pipe.Disconnect();
                }
            }
        }
        catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or ObjectDisposedException) { }
        finally { _pipe = null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        Volatile.Write(ref _schedule, null);
        _receive = null;
        _pipe?.Dispose();
        _lock.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
