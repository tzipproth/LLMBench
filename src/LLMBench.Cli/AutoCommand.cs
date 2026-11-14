using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LLMBench.Core;
using LLMBench.Core.Auto;
using LLMBench.Core.Llama;
using LLMBench.Core.Runs;
using LLMBench.Cli;
using LLMBench.Core.Stats;

/// <summary>
/// llmbench auto: for every model of the config start llama-server, wait until it is ready, run the benchmark,
/// stop the server, next model. Unattended; a failing model is reported and skipped.
/// </summary>
internal static partial class Commands
{
    private sealed class AutoResult
    {
        public required string Alias { get; init; }
        public string RunName { get; set; } = "";
        public string Status { get; set; } = "not started";
        public string? Detail { get; set; }
        public RunSummary? Summary { get; set; }
        public double Minutes { get; set; }
        public bool Ok => Status is "completed" or "already done";
    }

    private static readonly HashSet<string> AutoOnlyOptions = new(StringComparer.OrdinalIgnoreCase) { "prefix", "only", "workspace" };

    public static async Task<int> Auto(Workspace ws, Args a)
    {
        var cfgPath = ws.Resolve(a.Pos(0, "auto config file"));
        var cfg = AutoConfig.Load(cfgPath);
        var prefix = a.Get("prefix") ?? "v5-";
        var only = (a.Get("only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var o in only.Where(o => !cfg.Models.Any(m => m.Alias.Equals(o, StringComparison.OrdinalIgnoreCase))))
            throw new UsageException($"--only: model '{o}' is not in {cfgPath}.");
        var models = cfg.Models
            .Where(m => only.Count > 0 ? only.Contains(m.Alias, StringComparer.OrdinalIgnoreCase) : m.Enabled)
            .ToList();
        if (models.Count == 0) throw new BenchException("No model selected (all disabled?).");

        // run options for all models (command line), checked once before anything starts
        var global = new Args();
        foreach (var (k, v) in a.Options.Where(o => !AutoOnlyOptions.Contains(o.Key))) global.Options[k] = v;
        foreach (var m in models) BuildDefinition(ws, prefix + m.Alias, MergeOptions(global, m), null);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // second Ctrl+C kills the process
            e.Cancel = true;
            Console.WriteLine("\nStopping after cleanup (Ctrl+C again to kill) ...");
            cts.Cancel();
        };
        var ct = cts.Token;

        if (await ServerProcess.IsPortInUseAsync(cfg.ServerUrl, ct))
            throw new BenchException($"Something already answers at {cfg.ServerUrl}. Stop the running llama-server first.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logDir = Path.Combine(ws.RunsDir, "_auto", stamp);
        Directory.CreateDirectory(logDir);
        Console.WriteLine($"Auto run {stamp}: {models.Count} model(s), run names {prefix}<alias>, server logs in {logDir}");
        foreach (var m in models) Console.WriteLine($"  - {m.Alias}{(m.Bench is { Length: > 0 } b ? "  [" + b + "]" : "")}");

        var results = new List<AutoResult>();
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var r = new AutoResult { Alias = m.Alias, RunName = prefix + m.Alias };
            results.Add(r);
            if (ct.IsCancellationRequested) { r.Status = "not started (stopped)"; continue; }

            Console.WriteLine();
            WriteColored($"===== [{i + 1}/{models.Count}] {m.Alias} =====", ConsoleColor.Cyan);
            if (FindCompletedRun(ws, prefix + m.Alias) is { } done)
            {
                r.Status = "already done";
                r.RunName = done.Name;
                r.Summary = done.Summary;
                Console.WriteLine($"Skipped: run '{done.Name}' is already completed.");
                continue;
            }

            var runName = ws.Runs.Exists(prefix + m.Alias) || Directory.Exists(ws.Runs.RunDir(prefix + m.Alias))
                ? ws.Runs.NextRestartName(prefix + m.Alias) : prefix + m.Alias;
            r.RunName = runName;
            var opts = MergeOptions(global, m);
            var started = DateTime.Now;
            ServerProcess? server = null;
            try
            {
                var def = BuildDefinition(ws, runName, opts, null);
                if (!opts.Has("server")) def.ServerUrl = cfg.ServerUrl;

                server = ServerProcess.Start(string.IsNullOrWhiteSpace(m.ServerExe) ? cfg.ServerExe : m.ServerExe, CommandLine.Split(m.Args), Path.Combine(logDir, m.Alias + ".server.log"));
                Console.WriteLine($"llama-server started (pid {server.Id}), waiting until the model is loaded ...");
                var notReady = await server.WaitReadyAsync(cfg.ServerUrl, TimeSpan.FromSeconds(cfg.ReadyTimeoutSeconds), ct);
                if (notReady != null)
                {
                    r.Status = "server failed";
                    r.Detail = notReady + ServerHints(server);
                    WriteColored("ERROR: " + r.Detail, ConsoleColor.Red);
                    continue;
                }
                Console.WriteLine($"Server ready after {(DateTime.Now - started).TotalSeconds:0} s.");

                // models without a thinking switch in their template: send nothing (otherwise the probe fails)
                if (!opts.Has("thinking"))
                {
                    using var client = new LlamaClient(def.ServerUrl);
                    var props = await client.GetPropsAsync(ct);
                    var info = ChatTemplateInfo.Analyze(props?["chat_template"]?.GetValue<string>());
                    Console.WriteLine($"template: {info.Summary()}");
                    if (info.HasTemplate && !info.EnableThinking && def.Thinking != "default")
                    {
                        def.Thinking = "default";
                        Console.WriteLine("No enable_thinking in the template -> thinking 'default' (nothing sent).");
                    }
                }

                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = server.Exited.ContinueWith(_ => { try { runCts.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
                var (code, summary) = await ExecuteCore(ws, def, runCts.Token);
                r.Summary = summary;
                if (code == 0) r.Status = "completed";
                else if (ct.IsCancellationRequested) r.Status = "stopped";
                else if (server.HasExited)
                {
                    r.Status = "server crashed";
                    r.Detail = $"llama-server ended with exit code {server.ExitCode} during the run." + ServerHints(server);
                    WriteColored("ERROR: " + r.Detail, ConsoleColor.Red);
                }
                else r.Status = "incomplete";
            }
            catch (OperationCanceledException) { r.Status = "stopped"; }
            catch (BenchException ex)
            {
                r.Status = "error";
                r.Detail = ex.Message + (server != null ? ServerHints(server) : "");
                WriteColored("ERROR: " + r.Detail, ConsoleColor.Red);
            }
            finally
            {
                r.Minutes = (DateTime.Now - started).TotalMinutes;
                if (server != null)
                {
                    await server.StopAsync(TimeSpan.FromSeconds(cfg.StopTimeoutSeconds));
                    Console.WriteLine($"llama-server stopped. Log: {server.LogPath}");
                    server.Dispose();
                }
            }
            if (!await ServerProcess.WaitPortFreeAsync(cfg.ServerUrl, TimeSpan.FromSeconds(cfg.StopTimeoutSeconds), CancellationToken.None))
            {
                WriteColored($"ERROR: {cfg.ServerUrl} still answers after stopping the server - the remaining models are not started.", ConsoleColor.Red);
                cts.Cancel();
            }
        }

        var report = AutoReport(results, stamp, cfgPath);
        Console.WriteLine();
        Console.WriteLine(report);
        var reportPath = Path.Combine(logDir, "summary.txt");
        File.WriteAllText(reportPath, report);
        Console.WriteLine($"Summary: {reportPath}");
        return results.All(x => x.Ok) ? 0 : 1;
    }

    private static Args MergeOptions(Args global, AutoModel m)
    {
        var merged = new Args();
        foreach (var (k, v) in global.Options) merged.Options[k] = v;
        if (!string.IsNullOrWhiteSpace(m.Bench))
            foreach (var (k, v) in Args.Parse(CommandLine.Split(m.Bench)).Options) merged.Options[k] = v;
        return merged;
    }

    /// <summary>A completed run named base or base_NN (so a repeated auto call continues where it stopped).</summary>
    private static RunInfo? FindCompletedRun(Workspace ws, string baseName)
    {
        var pattern = new Regex("^" + Regex.Escape(baseName) + @"(_\d{2,})?$", RegexOptions.IgnoreCase);
        return ws.Runs.ListNames().Where(n => pattern.IsMatch(n))
            .Select(n => { try { return ws.Runs.GetInfo(n); } catch (Exception) { return null; } })
            .LastOrDefault(i => i?.State == RunState.Completed);
    }

    private static string ServerHints(ServerProcess s)
    {
        var sb = new StringBuilder();
        var errors = s.ErrorLines();
        if (errors.Count > 0) sb.Append("\n  error lines: ").Append(string.Join("\n    ", errors.TakeLast(5)));
        sb.Append("\n  last lines:\n    ").Append(string.Join("\n    ", s.Tail(8)));
        sb.Append($"\n  log: {s.LogPath}");
        return sb.ToString();
    }

    private static string AutoReport(List<AutoResult> results, string stamp, string cfgPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"Auto run {stamp} ({cfgPath})");
        sb.AppendLine($"{"model",-22} {"run",-28} {"status",-22} {"accuracy",9} {"inv+trunc",9} {"tok/ans",8} {"minutes",8}");
        foreach (var r in results)
        {
            var s = r.Summary;
            var acc = s?.Accuracy is { } x ? (x * 100).ToString("0.0", inv) + " %" : "-";
            var bad = s != null ? $"{s.Invalid + s.Truncated}" : "-";
            var tok = s != null && RunTiming.TokensPerAnswer(s) is { } t ? t.ToString("0", inv) : "-";
            var min = r.Status == "already done" ? "-" : r.Minutes.ToString("0.0", inv);
            sb.AppendLine($"{r.Alias,-22} {r.RunName,-28} {r.Status,-22} {acc,9} {bad,9} {tok,8} {min,8}");
        }
        foreach (var r in results.Where(r => r.Detail != null))
            sb.AppendLine().AppendLine($"{r.Alias}: {r.Detail}");
        sb.AppendLine().AppendLine($"{results.Count(r => r.Ok)} of {results.Count} model(s) done.");
        return sb.ToString();
    }
}
