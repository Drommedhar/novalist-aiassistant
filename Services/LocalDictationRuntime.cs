using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Novalist.Extensions.AiAssistant.Services;

public interface IDictationRuntime : IDisposable
{
    bool IsReady(string speech, string dialogue, string acceleration = "auto");
    Task PrepareAsync(string speech, string dialogue, Action<string, string> progress, CancellationToken token, string acceleration = "auto");
    Task<string> RequestAsync(string speech, string dialogue, object request, CancellationToken token, string acceleration = "auto");
}

/// <summary>
/// Owns a private Python environment and a persistent stdio worker. Model
/// downloads happen only during Prepare; the inference process is offline.
/// Kept outside Extensions so replacing an extension never deletes its models.
/// </summary>
public sealed class LocalDictationRuntime : IDictationRuntime
{
    public static readonly string[] SpeechModels = ["base", "small", "medium", "large-v3"];
    public static readonly string[] DialogueModels = ["1.7B", "4B"];
    private const string UvVersion = "0.9.7";
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer _idle;
    private readonly object _processLock = new();
    private Process? _worker;
    private string? _workerModels;
    private static readonly string[] ResourceFiles = ["worker.py", "accelerated.py", "requirements.txt", "requirements-gpu.txt",
        "requirements-cuda.txt", "requirements-rocm-windows.txt", "requirements-rocm-linux.txt", "requirements-mlx.txt"];
    private readonly Dictionary<string, byte[]> _resources = ResourceFiles.ToDictionary(file => file, Resource);
    private readonly string _recipe;
    // CPU already includes Silero VAD. GPU/MLX need one preparation pass for
    // the added packages. Gate exact recipes (LF/CRLF) so future dependency
    // changes cannot accidentally reuse an incompatible environment.
    private bool CompatibleRecipe(string installed, string backend) => installed == _recipe ||
        (backend == "cpu" && (_recipe is "1FAC0972CF15740A94C07A7E7E06384B0DE1AAA824BD5E2FC4E06835FB919A35"
            or "DCFD3418D493242F6964A1D0FBF56CA84741FD7C634EE4AC17B3047D27091A38")
        && (installed is "265BC0124E3B4C5541510EBD3B9652603B8D891C08AF099CEFC2392F9962D7AC"
            or "371BBE9496A758E93B8590C2349D01D46506FD5D6D2916F7FA481D8D83A128C9"
            or "B37107DB793DD7DC4573CEB84A2C78D533650AA91B43B3B868B63CDF8604BF8F"
            or "DCD002304D36BB1F0646ECE204238E31C66ADB4AF9CB3BCB14333F7DA79E42E6"));
    private string Venv(string backend) => Path.Combine(_root,
        (backend == "cpu" ? "venv" : "venv-" + backend)
        + (OperatingSystem.IsMacOS() ? "-" + RuntimeInformation.ProcessArchitecture : ""));
    private string Python(string backend) => Path.Combine(Venv(backend), OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
    private string Script => Path.Combine(_root, "worker.py");
    private string Marker(string speech, string dialogue, string backend)
        => Path.Combine(_root, $"ready-{speech}-{dialogue}-{backend}-{RuntimeInformation.ProcessArchitecture}.txt");

    public static string DefaultRoot => Path.Combine(
        Environment.GetEnvironmentVariable("NOVALIST_SETTINGS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Novalist"),
        "Models", "com.novalist.ai", "dictation");

    public LocalDictationRuntime(string? root = null)
    {
        _root = root ?? DefaultRoot;
        _recipe = Convert.ToHexString(SHA256.HashData(_resources.Values.SelectMany(bytes => bytes).ToArray()));
        _idle = new Timer(_ =>
        {
            if (!_gate.Wait(0)) return;
            try { StopWorker(); }
            finally { _gate.Release(); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private static byte[] Resource(string file)
    {
        using var stream = typeof(LocalDictationRuntime).Assembly.GetManifestResourceStream(
            "Novalist.Extensions.AiAssistant.Dictation." + file)
            ?? throw new InvalidOperationException("Dictation runtime is missing from the extension.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static void ValidateModels(string speech, string dialogue)
    {
        if (!SpeechModels.Contains(speech) || !DialogueModels.Contains(dialogue))
            throw new ArgumentException("Choose a supported local dictation model.");
    }

    public bool IsReady(string speech, string dialogue, string acceleration = "auto")
    {
        if (!SpeechModels.Contains(speech) || !DialogueModels.Contains(dialogue)) return false;
        try
        {
            var backend = DictationHardware.Resolve(acceleration);
            return File.Exists(Python(backend)) && File.Exists(Script) && File.Exists(Marker(speech, dialogue, backend))
                && CompatibleRecipe(File.ReadAllText(Marker(speech, dialogue, backend)), backend);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException) { return false; }
    }

    public async Task PrepareAsync(string speech, string dialogue, Action<string, string> progress, CancellationToken token, string acceleration = "auto")
    {
        ValidateModels(speech, dialogue);
        var backend = DictationHardware.Resolve(acceleration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            StopWorker();
            Directory.CreateDirectory(_root);
            var repair = IsReady(speech, dialogue, backend);
            // An interrupted repair must not leave an old success marker.
            File.Delete(Marker(speech, dialogue, backend));
            foreach (var (name, contents) in _resources)
                await File.WriteAllBytesAsync(Path.Combine(_root, name), contents, token);
            var requirements = Path.Combine(_root, DictationHardware.RequirementsFile(backend, OperatingSystem.IsWindows()));
            progress("runtime", backend.ToUpperInvariant());
            var uv = await DownloadUvAsync(token);
            var env = new Dictionary<string, string>
            {
                ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(_root, "python"),
                ["UV_CACHE_DIR"] = Path.Combine(_root, "cache"),
                ["UV_PYTHON_PREFERENCE"] = "only-managed",
                ["PYTHONUTF8"] = "1",
                ["HF_HUB_DISABLE_TELEMETRY"] = "1",
                ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1",
                ["HF_HOME"] = Path.Combine(_root, "hub"),
            };
            if (!File.Exists(Python(backend)))
                await RunAsync(uv, ["venv", "--python", "3.12", Venv(backend)], env,
                    line => progress("runtime", line), token);
            progress("packages", "");
            await RunAsync(uv, ["pip", "install", "--index-strategy", "unsafe-best-match", "--python", Python(backend), "--requirements", requirements], env,
                line => progress("packages", line), token);
            progress("models", "");
            await RunAsync(Python(backend), ["-u", Script, "prepare", _root, speech, dialogue, "--backend", backend, .. repair ? new[] { "--repair" } : []], env,
                line => progress("models", line), token);
            // Write only after both models have actually loaded successfully.
            await File.WriteAllTextAsync(Marker(speech, dialogue, backend), _recipe, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RequestAsync(string speech, string dialogue, object request, CancellationToken token, string acceleration = "auto")
    {
        ValidateModels(speech, dialogue);
        var backend = DictationHardware.Resolve(acceleration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsReady(speech, dialogue, backend)) throw new InvalidOperationException("Download the local dictation models in AI Assistant settings first.");
            if (File.ReadAllText(Marker(speech, dialogue, backend)) != _recipe)
            {
                StopWorker();
                foreach (var (name, contents) in _resources)
                    await File.WriteAllBytesAsync(Path.Combine(_root, name), contents, token);
                await File.WriteAllTextAsync(Marker(speech, dialogue, backend), _recipe, token);
            }
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            Process worker;
            lock (_processLock)
            {
                token.ThrowIfCancellationRequested();
                if (_worker == null || _worker.HasExited || _workerModels != speech + ":" + dialogue + ":" + backend)
                {
                    StopWorker();
                    var start = StartInfo(Python(backend), ["-u", Script, "serve", _root, speech, dialogue, "--backend", backend], new()
                    {
                        ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
                        ["HF_HUB_DISABLE_TELEMETRY"] = "1", ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1",
                        ["PYTHONUTF8"] = "1", ["TOKENIZERS_PARALLELISM"] = "false"
                    });
                    start.RedirectStandardInput = true;
                    _worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start local dictation.");
                    // Drain diagnostics without retaining any dictated content.
                    _worker.ErrorDataReceived += (_, _) => { };
                    _worker.BeginErrorReadLine();
                    _workerModels = speech + ":" + dialogue + ":" + backend;
                }
                worker = _worker;
            }
            using var cancel = token.Register(() => Kill(worker));
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
            await worker.StandardInput.FlushAsync(token);
            var reply = await worker.StandardOutput.ReadLineAsync(token);
            token.ThrowIfCancellationRequested();
            if (reply == null) throw new InvalidOperationException("The local dictation model stopped. Retry or repair the models in settings.");
            using var json = JsonDocument.Parse(reply);
            if (json.RootElement.TryGetProperty("error", out _))
                throw new InvalidOperationException("The local dictation model could not process this passage. Retry or repair the models in settings.");
            return json.RootElement.GetProperty("result").GetString() ?? string.Empty;
        }
        catch
        {
            StopWorker();
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            lock (_processLock)
                if (!_lifetime.IsCancellationRequested) _idle.Change(TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string[] args, Dictionary<string, string> env)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var (key, value) in env) info.Environment[key] = value;
        return info;
    }

    private static async Task RunAsync(string executable, string[] args, Dictionary<string, string> env,
        Action<string> progress, CancellationToken token)
    {
        using var process = Process.Start(StartInfo(executable, args, env))
            ?? throw new InvalidOperationException("Could not start the dictation installer.");
        using var cancel = token.Register(() => Kill(process));
        var tail = new Queue<string>();
        var lastUpdate = Stopwatch.StartNew();
        async Task Drain(StreamReader stream)
        {
            var buffer = new char[1024];
            var pending = new StringBuilder();
            void Report()
            {
                var line = pending.ToString();
                pending.Clear();
                if (line.Length == 0 || line.StartsWith("WARN Skipping file", StringComparison.Ordinal)) return;
                // Installer output only. Inference never calls this path.
                lock (tail)
                {
                    tail.Enqueue(line);
                    if (tail.Count > 8) tail.Dequeue();
                    if (lastUpdate.ElapsedMilliseconds < 200) return;
                    lastUpdate.Restart();
                    progress(line.Length > 300 ? line[^300..] : line);
                }
            }
            // Download bars use carriage returns, without newlines. Surface
            // them while a large file is downloading, not only at its end.
            while (await stream.ReadAsync(buffer, token) is var count && count > 0)
                for (var i = 0; i < count; i++)
                    if (buffer[i] is '\r' or '\n') Report();
                    else if (pending.Length < 4096) pending.Append(buffer[i]);
            Report();
        }
        await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(token));
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Local dictation setup failed: " + string.Join("\n", tail));
    }

    private async Task<string> DownloadUvAsync(CancellationToken token)
    {
        // Switching from Rosetta to a native Mac build must not reuse Intel uv
        // and silently create another Intel Python environment for MLX.
        var directory = Path.Combine(_root, "uv-" + UvVersion + "-" + RuntimeInformation.ProcessArchitecture);
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "uv.exe" : "uv");
        if (File.Exists(executable)) return executable;
        var cpu = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException("Local dictation requires a 64-bit computer.")
        };
        var asset = OperatingSystem.IsWindows() ? $"uv-{cpu}-pc-windows-msvc.zip"
            : OperatingSystem.IsMacOS() ? $"uv-{cpu}-apple-darwin.tar.gz" : $"uv-{cpu}-unknown-linux-gnu.tar.gz";
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, asset);
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var response = await http.GetAsync($"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{asset}",
                   HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(archive);
            await response.Content.CopyToAsync(file, token);
        }
        // Extract exactly the executable; archive paths never become file paths.
        var pending = executable + ".part";
        if (asset.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.Single(e => e.Name == "uv.exe");
            entry.ExtractToFile(pending, overwrite: true);
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                if (Path.GetFileName(entry.Name) != "uv" || entry.DataStream == null) continue;
                await using var output = File.Create(pending);
                await entry.DataStream.CopyToAsync(output, token);
                break;
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.Move(pending, executable, overwrite: true);
        File.Delete(archive);
        return executable;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void StopWorker()
    {
        lock (_processLock)
        {
            if (_worker == null) return;
            Kill(_worker);
            _worker.Dispose();
            _worker = null;
            _workerModels = null;
        }
    }

    public void Dispose()
    {
        lock (_processLock)
        {
            _lifetime.Cancel();
            _idle.Dispose();
            StopWorker();
        }
    }
}
