using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

public sealed class LocalModelRuntime : ILocalModelRuntime
{
    private readonly string _root;
    private readonly string _engineRoot;
    private readonly string _modelsRoot;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly StringBuilder _serverErrors = new();
    private Process? _process;
    private Uri? _endpoint;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset? _processStartTime;
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTimeOffset _lastCpuSampleTime = DateTimeOffset.UtcNow;
    private double _lastCpuUsage;
    /// <summary>How much of the card the engine is using, read from the machine rather than its log.</summary>
    private readonly GpuUsageSampler _gpuUsage = new();
    private double? _lastGpuUsage;
    /// <summary>Video memory the process holds, read live; the log's figure is only a fallback.</summary>
    private long _liveVideoMemoryBytes;
    /// <summary>The executable currently serving, which is also what was asked about the card.</summary>
    private string? _serverPath;
    private int _idleMinutes = 10;
    private bool _disposed;

    /// <summary>
    /// Which build of llama.cpp is wanted. On Windows and Linux the GPU build is Vulkan rather
    /// than CUDA: one archive covers NVIDIA, AMD and Intel, needs nothing installed beside the
    /// driver the machine already has. macOS ships its Metal-enabled build in the standard
    /// architecture archive. The two selections live in separate folders, so turning the switch
    /// off and on again costs nothing after the first download.
    /// </summary>
    private bool _preferGpu = true;
    private bool _runningOnGpu;
    private int _gpuLayers;
    private int _totalLayers;
    private string? _accelerator;
    private long _videoMemoryBytes;
    /// <summary>What the running server was started with, so asking for anything else restarts it.</summary>
    private LocalRuntimeOptions? _launchedWith;

    public LocalModelRuntime(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "local-ai");
        _engineRoot = Path.Combine(_root, "engine");
        _modelsRoot = Path.Combine(_root, "models");
        Directory.CreateDirectory(_engineRoot);
        Directory.CreateDirectory(_modelsRoot);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DAYDRIVE/1.0.0");
        Status = BuildStatus(Default, LocalModelRuntimeState.Stopped);
        _idleTimer = new Timer(CheckIdle, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private static LocalModelSelection Default => new(LocalModelCatalog.DefaultModelId);

    /// <summary>Path of the user's own GGUF, remembered so status checks work without settings.</summary>
    private string? _customModelPath;
    private string? _customProjectorPath;

    public IReadOnlyList<LocalModelDescriptor> Models => LocalModelCatalog.Models;
    public LocalModelRuntimeStatus Status { get; private set; }
    public event EventHandler<LocalModelStatusChangedEventArgs>? StatusChanged;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr Process, out PROCESS_MEMORY_COUNTERS_EX2 ppsmemCounters, uint cb);

    private static long GetExactProcessMemoryBytes(Process proc)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && proc.Handle != IntPtr.Zero)
            {
                var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
                if (K32GetProcessMemoryInfo(proc.Handle, out counters, counters.cb))
                {
                    var privateWs = (long)counters.PrivateWorkingSetSize.ToUInt64();
                    if (privateWs > 0) return privateWs;
                    var privateUsage = (long)counters.PrivateUsage.ToUInt64();
                    if (privateUsage > 0) return privateUsage;
                }
            }
        }
        catch { }

        return proc.PrivateMemorySize64 > 0 ? proc.PrivateMemorySize64 : proc.WorkingSet64;
    }

    /// <summary>
    /// What the card is doing for this process, kept at the last reading between samples so the
    /// figure does not blink to nothing on the ticks where the counter has not moved on yet.
    ///
    /// Asked whenever the GPU build is the one running, not when the log said layers were
    /// offloaded. That gate is what broke the whole graphics half of the monitor: the layer
    /// count is parsed out of what llama.cpp prints at startup, current builds print none of
    /// it, so the count stayed at zero, the card was never sampled, and the monitor reported a
    /// model sitting in six gigabytes of video memory as "not in use". Whether the model is on
    /// the card is now answered by the card itself.
    /// </summary>
    private void SampleGpu(Process proc)
    {
        if (!_runningOnGpu) return;
        var (usage, videoMemory) = _gpuUsage.Sample(proc.Id);
        if (usage is not null) _lastGpuUsage = usage;
        if (videoMemory > 0) _liveVideoMemoryBytes = videoMemory;
    }

    public LocalModelResourceMetrics? GetResourceMetrics()
    {
        var proc = _process;
        if (proc is null) return null;
        try
        {
            if (proc.HasExited) return null;
            proc.Refresh();
            var memoryBytes = GetExactProcessMemoryBytes(proc);
            var now = DateTimeOffset.UtcNow;
            var cpuTime = proc.TotalProcessorTime;
            var elapsedMs = (now - _lastCpuSampleTime).TotalMilliseconds;
            if (elapsedMs >= 300)
            {
                var cpuDeltaMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                _lastCpuUsage = Math.Clamp((cpuDeltaMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount))) * 100.0, 0.0, 100.0);
                _lastCpuTime = cpuTime;
                _lastCpuSampleTime = now;
            }

            var model = LocalModelCatalog.Find(Status.ModelId);
            var uptime = _processStartTime.HasValue ? (now - _processStartTime.Value) : TimeSpan.Zero;
            SampleGpu(proc);
            var (deviceName, deviceMemory) = _serverPath is not null && _runningOnGpu
                ? GpuDevice.Known(_serverPath)
                : (null, 0L);

            return new LocalModelResourceMetrics(
                MemoryBytes: memoryBytes,
                CpuPercent: Math.Round(_lastCpuUsage, 1),
                ModelName: model.DisplayName,
                Uptime: uptime,
                IsRunning: true,
                GpuLayers: _gpuLayers,
                TotalLayers: _totalLayers,
                // The engine's own name for the device when it printed one, and otherwise what
                // it says when asked to list what it can see.
                Accelerator: _accelerator ?? deviceName,
                // Live first: the log's figure is what the engine said it had allocated at
                // startup, which recent builds no longer print at all.
                VideoMemoryBytes: _liveVideoMemoryBytes > 0 ? _liveVideoMemoryBytes : _videoMemoryBytes,
                VideoMemoryTotalBytes: deviceMemory,
                GpuPercent: _runningOnGpu ? _lastGpuUsage : null,
                TotalMemoryBytes: SystemMemory.TotalBytes
            );
        }
        catch
        {
            return null;
        }
    }

    public LocalModelRuntimeStatus GetStatus(LocalModelSelection selection, string? customModelPath = null)
    {
        if (customModelPath is not null) _customModelPath = customModelPath;
        var state = Status.ModelId.Equals(selection.ModelId, StringComparison.OrdinalIgnoreCase)
            ? Status.State
            : LocalModelRuntimeState.Stopped;
        return BuildStatus(selection, state,
            Status.Progress, Status.DownloadedBytes, Status.TotalBytes, Status.Error);
    }

    public Task RefreshAsync(LocalModelSelection selection, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Publish(GetStatus(selection));
        return Task.CompletedTask;
    }

    /// <summary>Points the runtime at a GGUF the user picked from disk.</summary>
    public void UseCustomModel(string? modelPath, string? projectorPath)
    {
        _customModelPath = string.IsNullOrWhiteSpace(modelPath) ? null : modelPath;
        _customProjectorPath = string.IsNullOrWhiteSpace(projectorPath) ? null : projectorPath;
    }

    public async Task DownloadAsync(LocalModelSelection selection, bool includeProjector, bool useGpu = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (selection.IsCustom)
            throw new InvalidOperationException("A custom model is used from disk and is never downloaded.");

        var model = LocalModelCatalog.Find(selection.ModelId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _preferGpu = useGpu;
            await ResolveBackendAsync(selection, useGpu, cancellationToken);
            Directory.CreateDirectory(ModelDirectory(model));
            await DownloadFileAsync(model.ModelDownloadUri, ModelPath(model),
                selection, LocalModelRuntimeState.DownloadingModel, cancellationToken);
            if (includeProjector && model.ProjectorDownloadUri is { } projectorUri)
                await DownloadFileAsync(projectorUri, ProjectorPath(model)!,
                    selection, LocalModelRuntimeState.DownloadingProjector, cancellationToken);
            Publish(BuildStatus(selection, LocalModelRuntimeState.Stopped));
        }
        catch (OperationCanceledException)
        {
            Publish(BuildStatus(selection, LocalModelRuntimeState.Stopped));
            throw;
        }
        catch (Exception exception)
        {
            Publish(BuildStatus(selection, LocalModelRuntimeState.Error, error: exception.Message));
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(LocalModelSelection selection, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (selection.IsCustom) return;

        var model = LocalModelCatalog.Find(selection.ModelId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } &&
                Status.ModelId.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
                await StopProcessCoreAsync(false, cancellationToken);

            var modelDirectory = ModelDirectory(model);
            if (Directory.Exists(modelDirectory)) Directory.Delete(modelDirectory, true);
            Publish(BuildStatus(selection, LocalModelRuntimeState.Stopped));
        }
        finally { _gate.Release(); }
    }

    public async Task<Uri> EnsureReadyAsync(LocalModelSelection selection, LocalRuntimeOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        UseCustomModel(options.CustomModelPath, options.CustomProjectorPath);
        var model = LocalModelCatalog.Find(selection.ModelId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _idleMinutes = Math.Max(0, options.IdleMinutes);
            _lastActivity = DateTimeOffset.UtcNow;
            _preferGpu = options.UseGpu;
            // A server started with other numbers is not the server that was asked for: a new
            // context, thread count, image support or side of the GPU switch restarts it, the same
            // way changing the model does. It is compared with what was asked for last time, not
            // with where the engine ended up, so a machine whose card could not be used does not
            // restart the server on every message.
            if (_process is { HasExited: false } && _endpoint is not null &&
                _launchedWith is { } launched && launched with { IdleMinutes = 0 } == options with { IdleMinutes = 0 } &&
                Status.ModelId.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
            {
                Publish(BuildStatus(selection, LocalModelRuntimeState.Running));
                return _endpoint;
            }

            await StopProcessCoreAsync(false, cancellationToken);
            var backend = await ResolveBackendAsync(selection, options.UseGpu, cancellationToken);
            var serverPath = FindServerPath(BackendRoot(backend))
                ?? throw new InvalidOperationException("llama-server is not installed. Download a catalogue model once to install it.");

            var modelFile = ResolveModelPath(selection);
            if (!File.Exists(modelFile))
                throw new InvalidOperationException(selection.IsCustom
                    ? "The selected GGUF file was not found. Pick it again in settings."
                    : "The selected model is not downloaded yet.");

            var projectorFile = ResolveProjectorPath(selection);
            if (options.UseProjector && (projectorFile is null || !File.Exists(projectorFile)))
                throw new InvalidOperationException("The multimodal projector is not installed. Download vision support first.");

            Publish(BuildStatus(selection, LocalModelRuntimeState.Starting));
            var port = ReservePort();
            _endpoint = new Uri($"http://127.0.0.1:{port}");
            _serverErrors.Clear();
            var startInfo = new ProcessStartInfo(serverPath)
            {
                WorkingDirectory = Path.GetDirectoryName(serverPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(modelFile);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(Math.Clamp(options.ContextTokens, 2048, 131072).ToString());
            startInfo.ArgumentList.Add("-np");
            startInfo.ArgumentList.Add("1");
            // How much of the model goes on the card. A current engine works that out from what
            // is actually free there when the model loads (--fit, with the layer count left at
            // the default for it to choose), which beats any estimate made beforehand. "Every
            // layer" was the old answer, and on an 8 GB card with a 7 GB model it is the wrong
            // one: nothing is left for the context. An engine from before --fit still gets it,
            // being the only answer it understands — and it would refuse a switch it does not know.
            var flags = EngineFlags.Read(serverPath);
            if (backend == EngineBackend.Gpu && flags.Contains("--fit"))
            {
                startInfo.ArgumentList.Add("--fit");
                startInfo.ArgumentList.Add("on");
            }
            else
            {
                startInfo.ArgumentList.Add("-ngl");
                startInfo.ArgumentList.Add(backend == EngineBackend.Gpu ? "999" : "0");
            }
            // Tool calling needs the model's own chat template.
            startInfo.ArgumentList.Add("--jinja");
            // Keep what was already read. The engine reuses the prompt it still holds as far as
            // the new one matches it, and this lets it recover the tail as well by shifting the
            // cache past a chunk that changed in the middle — which is the difference between
            // re-reading a five-thousand-token preamble on every message and re-reading none of
            // it. The prompt is built to make that possible; this is the half that uses it.
            startInfo.ArgumentList.Add("--cache-reuse");
            startInfo.ArgumentList.Add("256");
            if (options.Threads > 0)
            {
                // llama.cpp otherwise counts hyper-threads, which is usually slower than
                // pinning the physical core count.
                startInfo.ArgumentList.Add("-t");
                startInfo.ArgumentList.Add(Math.Clamp(options.Threads, 1, 256).ToString());
                // Reading the prompt is the one part that does get faster with every core, the
                // efficiency ones included, so it is given all of them.
                var batchThreads = HardwareProbe.PhysicalCores;
                if (batchThreads > options.Threads && flags.Contains("-tb"))
                {
                    startInfo.ArgumentList.Add("-tb");
                    startInfo.ArgumentList.Add(Math.Clamp(batchThreads, 1, 256).ToString());
                }
            }
            if (options.UseProjector && projectorFile is not null)
            {
                startInfo.ArgumentList.Add("--mmproj");
                startInfo.ArgumentList.Add(projectorFile);
            }

            _runningOnGpu = backend == EngineBackend.Gpu;
            _serverPath = serverPath;
            _gpuLayers = 0;
            _totalLayers = 0;
            _accelerator = null;
            _videoMemoryBytes = 0;
            _liveVideoMemoryBytes = 0;
            // Which card, and how big it is. Asked once per engine folder, off this thread: the
            // model behind it takes seconds to load and the answer is only wanted by a monitor.
            if (backend == EngineBackend.Gpu) GpuDevice.Probe(serverPath);
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += CaptureServerOutput;
            _process.ErrorDataReceived += CaptureServerOutput;
            if (!_process.Start()) throw new InvalidOperationException("llama-server could not be started.");
            _processStartTime = DateTimeOffset.UtcNow;
            _lastCpuTime = TimeSpan.Zero;
            _lastCpuSampleTime = DateTimeOffset.UtcNow;
            _lastCpuUsage = 0.0;
            _lastGpuUsage = null;
            _gpuUsage.Reset();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await WaitUntilHealthyAsync(_process, _endpoint, cancellationToken);
            _launchedWith = options;
            Publish(BuildStatus(selection, LocalModelRuntimeState.Running));
            return _endpoint;
        }
        catch (OperationCanceledException)
        {
            await StopProcessCoreAsync(false, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await StopProcessCoreAsync(false, CancellationToken.None);
            Publish(BuildStatus(selection, LocalModelRuntimeState.Error, error: exception.Message));
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(bool sleeping = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try { await StopProcessCoreAsync(sleeping, cancellationToken); }
        finally { _gate.Release(); }
    }

    public void NotifyActivity(int idleMinutes)
    {
        _idleMinutes = Math.Max(0, idleMinutes);
        _lastActivity = DateTimeOffset.UtcNow;
    }

    private enum EngineBackend { Cpu, Gpu }

    /// <summary>Each build keeps its own folder, so the switch never re-downloads the other one.</summary>
    private string BackendRoot(EngineBackend backend)
        => Path.Combine(_engineRoot, backend == EngineBackend.Gpu ? "gpu" : "current");

    /// <summary>
    /// The build this run will actually use. Asking for the card is a preference, not a promise:
    /// a machine whose architecture has no GPU archive, or a download that will not come, gets
    /// the processor build and an answer rather than an error.
    /// </summary>
    private async Task<EngineBackend> ResolveBackendAsync(LocalModelSelection selection, bool useGpu,
        CancellationToken cancellationToken)
    {
        if (useGpu)
        {
            if (FindServerPath(BackendRoot(EngineBackend.Gpu)) is not null) return EngineBackend.Gpu;
            try
            {
                await EnsureEngineInstalledAsync(selection, EngineBackend.Gpu, cancellationToken);
                return EngineBackend.Gpu;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                lock (_serverErrors) _serverErrors.AppendLine($"GPU engine unavailable: {exception.Message}");
            }
        }
        await EnsureEngineInstalledAsync(selection, EngineBackend.Cpu, cancellationToken);
        return EngineBackend.Cpu;
    }

    private async Task EnsureEngineInstalledAsync(LocalModelSelection selection, EngineBackend backend,
        CancellationToken cancellationToken)
    {
        var current = BackendRoot(backend);
        if (FindServerPath(current) is not null) return;
        var (version, assetUrl, extension) = await ResolveLatestEngineAssetAsync(backend, cancellationToken);
        var archive = Path.Combine(_engineRoot, $"llama-{version}-{backend.ToString().ToLowerInvariant()}{extension}");
        await DownloadFileAsync(assetUrl, archive, selection, LocalModelRuntimeState.DownloadingEngine, cancellationToken);

        Publish(BuildStatus(selection, LocalModelRuntimeState.ExtractingEngine));
        var extracting = Path.Combine(_engineRoot, ".extracting");
        if (Directory.Exists(extracting)) Directory.Delete(extracting, true);
        Directory.CreateDirectory(extracting);
        if (extension == ".zip")
            ZipFile.ExtractToDirectory(archive, extracting, true);
        else
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, extracting, true);
        }
        if (FindServerPath(extracting) is null) throw new InvalidDataException("The llama.cpp archive does not contain llama-server.");
        if (Directory.Exists(current)) Directory.Delete(current, true);
        Directory.Move(extracting, current);
        try { File.Delete(archive); } catch { }
    }

    private async Task<(string Version, Uri Url, string Extension)> ResolveLatestEngineAssetAsync(
        EngineBackend backend, CancellationToken cancellationToken)
    {
        const string releasesApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases";
        using var latest = await ReadJsonAsync($"{releasesApi}/latest", cancellationToken);
        if (TryResolveEngineAsset(latest.RootElement, backend, out var direct)) return direct;

        // Stable semantic releases now contain only a tiny pointer to the tested nightly
        // build. Looking only at /releases/latest therefore made every first model download
        // fail with "compatible build was not found" even though the CPU archive existed.
        if (TryFindAsset(latest.RootElement, "nightly-tag.txt", out var nightlyPointer))
        {
            var nightlyTag = (await _http.GetStringAsync(nightlyPointer, cancellationToken)).Trim();
            if (!string.IsNullOrWhiteSpace(nightlyTag))
            {
                using var nightly = await ReadJsonAsync(
                    $"{releasesApi}/tags/{Uri.EscapeDataString(nightlyTag)}", cancellationToken);
                if (TryResolveEngineAsset(nightly.RootElement, backend, out var pointed)) return pointed;
            }
        }

        // Keep a final compatibility fallback for a temporarily incomplete latest release.
        // GitHub returns releases newest-first, so this normally stops at the first nightly.
        using var recent = await ReadJsonAsync($"{releasesApi}?per_page=10", cancellationToken);
        foreach (var release in recent.RootElement.EnumerateArray())
            if (TryResolveEngineAsset(release, backend, out var compatible)) return compatible;

        throw new InvalidOperationException(
            $"A compatible llama.cpp {(backend == EngineBackend.Gpu ? "GPU" : "CPU")} build was not found in recent releases.");
    }

    private async Task<JsonDocument> ReadJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static bool TryResolveEngineAsset(JsonElement release, EngineBackend backend,
        out (string Version, Uri Url, string Extension) result)
    {
        result = default;
        var tag = release.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? "latest"
            : "latest";
        var suffix = EngineAssetSuffix(backend);
        if (!release.TryGetProperty("assets", out var assets)) return false;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            result = (tag, url, name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz");
            return true;
        }
        return false;
    }

    /// <summary>
    /// The archive that matches this machine. llama.cpp's macOS archives include Metal support;
    /// Vulkan is only published for the 64-bit desktop architectures, so an ARM Windows/Linux
    /// machine asking for the card is told there is no such build and quietly ends up on the
    /// processor one.
    /// </summary>
    private static string EngineAssetSuffix(EngineBackend backend)
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return arm ? "bin-macos-arm64.tar.gz" : "bin-macos-x64.tar.gz";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (backend == EngineBackend.Gpu)
                return arm
                    ? throw new PlatformNotSupportedException("llama.cpp publishes no GPU build for this architecture.")
                    : "bin-win-vulkan-x64.zip";
            return arm ? "bin-win-cpu-arm64.zip" : "bin-win-cpu-x64.zip";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (backend == EngineBackend.Gpu)
                return arm
                    ? throw new PlatformNotSupportedException("llama.cpp publishes no GPU build for this architecture.")
                    : "bin-ubuntu-vulkan-x64.tar.gz";
            return arm ? "bin-ubuntu-arm64.tar.gz" : "bin-ubuntu-x64.tar.gz";
        }
        throw new PlatformNotSupportedException("Automatic llama.cpp installation currently supports Windows, macOS and Linux.");
    }

    private static bool TryFindAsset(JsonElement release, string assetName, out Uri uri)
    {
        uri = null!;
        if (!release.TryGetProperty("assets", out var assets)) return false;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!assetName.Equals(asset.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var parsed)
                || parsed is null) continue;
            uri = parsed;
            return true;
        }
        return false;
    }

    private async Task DownloadFileAsync(Uri uri, string destination, LocalModelSelection selection,
        LocalModelRuntimeState state, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 1_000_000) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            response.Content.Headers.ContentRange?.Length is { } remoteLength && remoteLength == existing)
        {
            File.Move(partial, destination, true);
            Publish(BuildStatus(selection, state, 100, existing, existing));
            return;
        }
        response.EnsureSuccessStatusCode();
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) existing = 0;
        long? total = response.Content.Headers.ContentLength is { } length ? existing + length : null;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 256, true);
        var buffer = new byte[1024 * 256];
        var downloaded = existing;
        var lastUpdate = Stopwatch.StartNew();
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (lastUpdate.ElapsedMilliseconds < 120) continue;
            Publish(BuildStatus(selection, state, total is > 0 ? downloaded * 100d / total.Value : 0, downloaded, total));
            lastUpdate.Restart();
        }
        await target.FlushAsync(cancellationToken);
        target.Close();
        File.Move(partial, destination, true);
        Publish(BuildStatus(selection, state, 100, downloaded, total));
    }

    private async Task WaitUntilHealthyAsync(Process process, Uri endpoint, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"llama-server stopped during startup. {ServerErrorTail()}");
            try
            {
                using var response = await _http.GetAsync(new Uri(endpoint, "/health"), cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"The local model did not become ready in time. {ServerErrorTail()}");
    }

    private async Task StopProcessCoreAsync(bool sleeping, CancellationToken cancellationToken)
    {
        var modelId = Status.ModelId;
        var process = _process;
        _process = null;
        _endpoint = null;
        _processStartTime = null;
        _lastCpuTime = TimeSpan.Zero;
        _lastCpuUsage = 0.0;
        _lastGpuUsage = null;
        _liveVideoMemoryBytes = 0;
        _serverPath = null;
        _gpuUsage.Reset();
        _gpuLayers = 0;
        _totalLayers = 0;
        _accelerator = null;
        _videoMemoryBytes = 0;
        _launchedWith = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        Publish(BuildStatus(new LocalModelSelection(Status.ModelId),
            sleeping ? LocalModelRuntimeState.Sleeping : LocalModelRuntimeState.Stopped));
    }

    private async void CheckIdle(object? _)
    {
        if (_disposed || _idleMinutes <= 0 || Status.State != LocalModelRuntimeState.Running ||
            DateTimeOffset.UtcNow - _lastActivity < TimeSpan.FromMinutes(_idleMinutes)) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (Status.State == LocalModelRuntimeState.Running &&
                DateTimeOffset.UtcNow - _lastActivity >= TimeSpan.FromMinutes(_idleMinutes))
                await StopProcessCoreAsync(true, CancellationToken.None);
        }
        catch { }
        finally { _gate.Release(); }
    }

    private void CaptureServerOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        ReadAccelerationLine(args.Data);
        lock (_serverErrors)
        {
            _serverErrors.AppendLine(args.Data);
            if (_serverErrors.Length > 12_000) _serverErrors.Remove(0, _serverErrors.Length - 8_000);
        }
    }

    private static readonly Regex OffloadedLayers =
        new(@"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers\s+to\s+GPU", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VulkanDevice =
        new(@"ggml_vulkan:\s*\d+\s*=\s*(.+?)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CudaDevice =
        new(@"Device\s+\d+:\s*([^,]+)", RegexOptions.Compiled);
    private static readonly Regex DeviceBuffer =
        new(@"(\S+)\s+(?:model|KV|compute)\s+buffer size\s*=\s*([0-9.]+)\s*MiB",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What the engine says about the device it found, read from the log it already prints.
    /// Nothing else on the machine can answer "is this model actually on the card" — the
    /// process owns the allocation and the driver reports it per adapter, not per process — and
    /// llama.cpp states it plainly on the way up.
    /// </summary>
    private void ReadAccelerationLine(string line)
    {
        if (OffloadedLayers.Match(line) is { Success: true } offload)
        {
            _gpuLayers = int.TryParse(offload.Groups[1].Value, out var offloaded) ? offloaded : 0;
            _totalLayers = int.TryParse(offload.Groups[2].Value, out var total) ? total : 0;
            return;
        }
        if (_accelerator is null && VulkanDevice.Match(line) is { Success: true } vulkan)
        {
            _accelerator = vulkan.Groups[1].Value.Trim();
            return;
        }
        if (_accelerator is null && line.Contains("CUDA", StringComparison.OrdinalIgnoreCase) &&
            CudaDevice.Match(line) is { Success: true } cuda)
        {
            _accelerator = cuda.Groups[1].Value.Trim();
            return;
        }
        if (DeviceBuffer.Match(line) is { Success: true } buffer &&
            !buffer.Groups[1].Value.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(buffer.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mebibytes))
            _videoMemoryBytes += (long)(mebibytes * 1024 * 1024);
    }

    private string ServerErrorTail()
    {
        lock (_serverErrors) return _serverErrors.ToString().Trim();
    }

    private LocalModelRuntimeStatus BuildStatus(LocalModelSelection selection, LocalModelRuntimeState state,
        double progress = 0, long downloaded = 0, long? total = null, string? error = null)
    {
        var installed = File.Exists(ResolveModelPath(selection));
        var projector = ResolveProjectorPath(selection);
        return new LocalModelRuntimeStatus(selection.ModelId, state,
            FindServerPath() is not null, installed, projector is not null && File.Exists(projector),
            progress, downloaded, total, error);
    }

    private void Publish(LocalModelRuntimeStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, new(status));
    }

    /// <summary>
    /// Named after the precision, as every download has been since there were several: the Q5
    /// folder an earlier release filled is found where it always was, and nothing is fetched twice.
    /// </summary>
    private string ModelDirectory(LocalModelDescriptor model)
        => Path.Combine(_modelsRoot, model.Id, model.Quant.Tag);

    private string ModelPath(LocalModelDescriptor model)
        => Path.Combine(ModelDirectory(model), model.FileName);

    private string? ProjectorPath(LocalModelDescriptor model)
        => model.ProjectorFileName is null
            ? null
            : Path.Combine(ModelDirectory(model), model.ProjectorFileName);

    private string ResolveModelPath(LocalModelSelection selection)
        => selection.IsCustom
            ? _customModelPath ?? string.Empty
            : ModelPath(LocalModelCatalog.Find(selection.ModelId));

    private string? ResolveProjectorPath(LocalModelSelection selection)
        => selection.IsCustom
            ? _customProjectorPath
            : ProjectorPath(LocalModelCatalog.Find(selection.ModelId));

    public string? ModelFilePath(LocalModelSelection selection)
    {
        var path = ResolveModelPath(selection);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The GPU build is the one asked about the card: it names the device and says what is free
    /// on it right now, which is the figure the tuner needs and the one the system will not give.
    /// </summary>
    public HardwareProfile ReadHardware() => HardwareProbe.Read(FindServerPath(BackendRoot(EngineBackend.Gpu)));

    public long RetiredDownloadBytes() => RetiredDownloadDirectories().Sum(DirectorySize);

    public async Task<long> DeleteRetiredDownloadsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            long freed = 0;
            foreach (var directory in RetiredDownloadDirectories())
            {
                var size = DirectorySize(directory);
                try
                {
                    Directory.Delete(directory, recursive: true);
                    freed += size;
                }
                catch (IOException) { /* In use or half-deleted; the next attempt finishes it. */ }
                catch (UnauthorizedAccessException) { }
            }
            return freed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The precision folders earlier releases filled beside the Q5 one every model uses now.</summary>
    private List<string> RetiredDownloadDirectories()
        => Directory.Exists(_modelsRoot)
            ? Directory.EnumerateDirectories(_modelsRoot)
                .SelectMany(model => LocalModelCatalog.RetiredQuantTags.Select(tag => Path.Combine(model, tag)))
                .Where(Directory.Exists)
                .ToList()
            : [];

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
    /// <summary>
    /// Whether there is an engine at all. Either build answers: the preferred one is installed
    /// when the model is started, and a card that turns out to be unusable must not make an
    /// already downloaded model look uninstallable.
    /// </summary>
    private string? FindServerPath()
        => FindServerPath(BackendRoot(_preferGpu ? EngineBackend.Gpu : EngineBackend.Cpu))
           ?? FindServerPath(BackendRoot(_preferGpu ? EngineBackend.Cpu : EngineBackend.Gpu));
    private static string? FindServerPath(string root)
    {
        if (!Directory.Exists(root)) return null;
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "llama-server.exe" : "llama-server";
        return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        try { StopProcessCoreAsync(false, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _gpuUsage.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}
