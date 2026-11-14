using System.Diagnostics;
using System.Text.RegularExpressions;
using LLMBench.Core.Llama;

namespace LLMBench.Core.Auto;

/// <summary>
/// A llama-server started as a child process: output goes to a log file, readiness is checked with /health,
/// and the process is killed when the model is done.
/// </summary>
public sealed partial class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly Queue<string> _tail = new();
    private readonly List<string> _errors = [];
    private readonly object _lock = new();
    private bool _closed;

    public string LogPath { get; }
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Signalled when the process ends (for whatever reason).</summary>
    public Task Exited { get; }

    // llama.cpp log lines: "0.10.803.485 E srv ...": level letter after the timestamp
    [GeneratedRegex(@"^\s*[\d.:]+\s+E\s|\berror\b|\bfailed\b|out of memory", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    private ServerProcess(Process p, StreamWriter log, string logPath)
    {
        _process = p;
        _log = log;
        LogPath = logPath;
        Exited = p.WaitForExitAsync();
    }

    public static ServerProcess Start(string exe, IEnumerable<string> args, string logPath)
    {
        if (!File.Exists(exe)) throw new BenchException($"llama-server not found: {exe}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        log.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} start: {exe} {string.Join(' ', psi.ArgumentList.Select(Quote))}");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        ServerProcess? sp = null;
        p.OutputDataReceived += (_, e) => sp?.OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => sp?.OnLine(e.Data);
        try
        {
            if (!p.Start()) throw new BenchException($"Could not start {exe}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Dispose();
            throw new BenchException($"Could not start {exe}: {ex.Message}");
        }
        sp = new ServerProcess(p, log, logPath);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return sp;
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        lock (_lock)
        {
            if (_closed) return;
            _log.WriteLine(line);
            _tail.Enqueue(line);
            while (_tail.Count > 30) _tail.Dequeue();
            if (ErrorLine().IsMatch(line) && _errors.Count < 50) _errors.Add(line.Trim());
        }
    }

    /// <summary>Last lines of the server output (for error reports).</summary>
    public List<string> Tail(int n = 15) { lock (_lock) return _tail.TakeLast(n).ToList(); }

    /// <summary>Lines that look like errors (log level E, "error", "failed", "out of memory").</summary>
    public List<string> ErrorLines() { lock (_lock) return [.. _errors]; }

    /// <summary>
    /// Waits until /health answers ok. Returns null when ready, otherwise the reason (process ended, timeout).
    /// </summary>
    public async Task<string?> WaitReadyAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new LlamaClient(url);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited) return $"llama-server ended with exit code {_process.ExitCode} before it was ready.";
            var h = await client.GetHealthAsync(ct, 5).ConfigureAwait(false);
            if (h.Ok) return null;
            await Task.WhenAny(Task.Delay(2000, ct), Exited).ConfigureAwait(false);
        }
        return $"llama-server was not ready after {timeout.TotalSeconds:0} s (model still loading?).";
    }

    /// <summary>Kills the process (with child processes) and waits until it has ended.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await Task.WhenAny(Exited, Task.Delay(timeout)).ConfigureAwait(false);
        }
        lock (_lock) if (!_closed) _log.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} stopped (exit code {(_process.HasExited ? _process.ExitCode : "?")})");
    }

    /// <summary>True when something answers HTTP at the URL (another server is running there).</summary>
    public static async Task<bool> IsPortInUseAsync(string url, CancellationToken ct)
    {
        using var client = new LlamaClient(url);
        var h = await client.GetHealthAsync(ct, 3).ConfigureAwait(false);
        return h.HttpStatus != null;
    }

    /// <summary>Waits until nothing answers at the URL any more.</summary>
    public static async Task<bool> WaitPortFreeAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!await IsPortInUseAsync(url, ct).ConfigureAwait(false)) return true;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static string Quote(string s) => s.Length > 0 && s.All(ch => !char.IsWhiteSpace(ch) && ch is not ('"' or '\'')) ? s : "'" + s.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        lock (_lock) { _closed = true; _log.Dispose(); }
        _process.Dispose();
    }
}
