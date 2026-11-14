using System.Reflection;
using System.Text.Json.Nodes;
using LLMBench.Core.Llama;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Core.Runs;

public enum RunEventKind { Log, Warning, ItemStarted, ItemCompleted, Finished }

public sealed class RunEvent
{
    public required RunEventKind Kind { get; init; }
    public string? Message { get; init; }
    public PlannedItem? Item { get; init; }
    public ItemRecord? Record { get; init; }
    public RunSummary? Summary { get; init; }
    public RunHeader? Header { get; init; }
    public int Total { get; init; }
}

/// <summary>Executes one run definition against the server: preflight, serial requests, scoring, JSONL output.</summary>
public sealed class BenchRunner(Workspace ws)
{
    public static string Version =>
        typeof(BenchRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";

    private const int MaxConsecutiveErrors = 3;

    public RunPlan Prepare(RunDefinition def) => RunPlanner.Build(ws, def);

    /// <summary>
    /// Runs the plan. Preflight problems throw <see cref="BenchException"/> before results.jsonl is created
    /// (the run stays a draft). Cancelling via <paramref name="ct"/> stops after the current request is aborted;
    /// the summary is then written with aborted = true.
    /// </summary>
    public async Task<RunSummary> RunAsync(RunDefinition def, RunPlan plan, IProgress<RunEvent>? progress, CancellationToken ct)
    {
        void Log(string msg) => progress?.Report(new RunEvent { Kind = RunEventKind.Log, Message = msg, Total = plan.Items.Count });
        void Warn(string msg) => progress?.Report(new RunEvent { Kind = RunEventKind.Warning, Message = msg, Total = plan.Items.Count });

        var store = ws.Runs;
        if (store.HasResults(def.Name))
            throw new BenchException($"Run '{def.Name}' already has results. Use restart to run the same parameters again.");
        store.Save(def);

        using var client = new LlamaClient(def.ServerUrl);
        var qs = QuestionSet.FromPlan(plan);
        var header = new RunHeader
        {
            RunName = def.Name,
            RunId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            StartedAt = DateTime.Now,
            LlmBenchVersion = Version,
            MachineName = Environment.MachineName,
            Definition = def.Clone(),
            SystemPromptFile = plan.SystemPromptFile,
            SystemPrompt = plan.SystemPrompt,
            SystemPromptSha256 = plan.SystemPrompt == null ? null : Json.Sha256(plan.SystemPrompt),
            CatalogFile = plan.Catalog.RelativePath,
            CatalogSha256 = plan.Catalog.Sha256,
            CatalogQuestions = plan.Catalog.Questions.Count,
            PlannedItems = plan.Items.Count,
            QuestionSetHash = qs.Hash,
            QuestionSetCount = qs.Count,
            SkippedVariants = plan.Skipped,
        };

        // ---- Preflight -------------------------------------------------------------
        Log($"Checking server {def.ServerUrl} ...");
        var health = await client.GetHealthAsync(ct).ConfigureAwait(false);
        if (!health.Ok) throw new BenchException($"Server not ready: {health.Message}");
        header.ServerHealth = health.Message;

        JsonObject? props = null;
        try { props = await client.GetPropsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { header.Warnings.Add($"GET /props failed: {ex.Message}"); }
        if (props != null)
        {
            header.ServerProps = props;
            header.ModelAlias = props["model_alias"]?.ToString();
            header.ModelPath = props["model_path"]?.ToString();
            header.ContextSize = props["default_generation_settings"]?["n_ctx"]?.GetValue<int>();
            header.VisionAvailable = props["modalities"]?["vision"]?.GetValue<bool>();
        }
        else header.Warnings.Add("Server properties (/props) not available; server sampler defaults are unknown.");

        Log($"Server: model {header.ModelAlias ?? "?"} ({header.ModelPath ?? "?"}), context {header.ContextSize?.ToString() ?? "?"}, vision {header.VisionAvailable?.ToString() ?? "?"}");

        // empty model name: take the alias of the model the server actually runs (no mislabeled runs)
        if (string.IsNullOrWhiteSpace(def.Model) && !string.IsNullOrWhiteSpace(header.ModelAlias))
        {
            def.Model = header.ModelAlias;
            header.Definition = def.Clone();
            store.Save(def);
            Log($"Model name taken from the server: {def.Model}");
        }

        if (def.ExpectedContextSize.HasValue && header.ContextSize.HasValue && def.ExpectedContextSize != header.ContextSize)
            throw new BenchException($"Context size mismatch: run expects {def.ExpectedContextSize}, server has {header.ContextSize}.");
        // llama-server answers with whatever model is loaded, regardless of the "model" field: a mismatch means the
        // run would be stored under a wrong model name. Abort instead of producing a mislabeled run.
        if (!string.IsNullOrWhiteSpace(def.Model) && header.ModelAlias != null && !string.Equals(def.Model, header.ModelAlias, StringComparison.Ordinal))
            throw new BenchException($"The server runs model '{header.ModelAlias}' ({Path.GetFileName(header.ModelPath ?? "")}), but this run is for model '{def.Model}'. " +
                                     "Start the right model on the server, or change the model name of the run (button 'Use server alias').");

        Log("Warm-up request ...");
        var warmReq = RequestBuilder.Build(def, null, "What is 2 + 3? Reply with exactly one line and nothing else:\nFinal Answer: <number>", def.Seed);
        var warm = await client.ChatAsync(warmReq, TimeSpan.FromSeconds(Math.Max(def.TimeoutSeconds, 600)), ct).ConfigureAwait(false);
        header.Warmup = new WarmupInfo
        {
            Ok = warm.Success,
            ElapsedSeconds = warm.ElapsedSeconds,
            Content = warm.Content,
            ReasoningContent = warm.ReasoningContent,
            FinishReason = warm.FinishReason,
            Error = warm.Error,
        };
        if (!warm.Success) throw new BenchException($"Warm-up request failed: {warm.Error}");
        Log($"Warm-up ok ({warm.ElapsedSeconds:0.0} s): {Shorten(warm.Content, 80)}");
        if (!string.IsNullOrEmpty(warm.ReasoningContent) && def.Thinking == "off")
            header.Warnings.Add("Thinking is 'off' but the warm-up reply contains reasoning_content.");

        if (def.Thinking is "on" or "off")
        {
            Log($"Thinking probe for thinking = {def.Thinking} (same question with ON and OFF fragment) ...");
            var probe = await ThinkingProbe.RunAsync(client, def, ct).ConfigureAwait(false);
            header.ThinkingProbe = probe;
            header.ThinkingVerified = probe.Verified;
            Log($"Thinking probe: {(probe.Verified ? "verified" : "NOT verified")} - {probe.Note}");
            if (!probe.Verified)
                header.Warnings.Add($"Thinking switch NOT verified: {probe.Note} An ON/OFF comparison is not meaningful.");
        }

        if (plan.Items.Any(i => i.Variant.Image != null) && header.VisionAvailable != true)
            header.Warnings.Add("Image questions will be skipped: server reports no vision support.");

        foreach (var w in header.Warnings) Warn(w);

        // ---- Items ---------------------------------------------------------------------
        var records = new List<ItemRecord>();
        var started = DateTime.Now;
        string? abortReason = null;
        bool aborted = false;
        int consecutiveErrors = 0;

        using (var writer = new ResultWriter(store.ResultsPath(def.Name)))
        {
            writer.Write(header);
            progress?.Report(new RunEvent { Kind = RunEventKind.Log, Header = header, Message = $"Run started: {plan.Items.Count} items.", Total = plan.Items.Count });

            foreach (var item in plan.Items)
            {
                if (ct.IsCancellationRequested) { aborted = true; abortReason = "stopped by user"; break; }
                progress?.Report(new RunEvent { Kind = RunEventKind.ItemStarted, Item = item, Total = plan.Items.Count });

                ItemRecord rec;
                try
                {
                    rec = await ExecuteItemAsync(client, def, item, header.VisionAvailable == true, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    aborted = true;
                    abortReason = "stopped by user";
                    break;
                }

                records.Add(rec);
                writer.Write(rec);
                progress?.Report(new RunEvent { Kind = RunEventKind.ItemCompleted, Item = item, Record = rec, Total = plan.Items.Count });

                consecutiveErrors = rec.Status == ItemStatus.Error ? consecutiveErrors + 1 : 0;
                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    aborted = true;
                    abortReason = $"{MaxConsecutiveErrors} consecutive errors (last: {rec.Error})";
                    Warn($"Run aborted: {abortReason}");
                    break;
                }
            }

            var summary = SummaryBuilder.Build(records, plan.Items.Count);
            summary.Aborted = aborted;
            summary.AbortReason = abortReason;
            summary.Completed = !aborted && records.Count == plan.Items.Count;
            summary.FinishedAt = DateTime.Now;
            summary.DurationSeconds = (summary.FinishedAt - started).TotalSeconds;
            writer.Write(summary);
            progress?.Report(new RunEvent { Kind = RunEventKind.Finished, Summary = summary, Header = header, Total = plan.Items.Count });
            return summary;
        }
    }

    private async Task<ItemRecord> ExecuteItemAsync(LlamaClient client, RunDefinition def, PlannedItem item, bool vision, CancellationToken ct)
    {
        var v = item.Variant;
        var rec = new ItemRecord
        {
            Index = item.Index,
            QuestionId = item.Question.Id,
            VariantId = v.Id,
            VariantType = v.IsChoice ? "choice" : "free",
            VariantKind = v.Kind,
            AnswerType = v.IsChoice ? null : (v.AnswerType ?? "text"),
            Category = item.Question.Category,
            Tier = v.Tier ?? item.Question.Tier,
            Tags = item.Question.Tags,
            Repetition = item.Repetition,
            Seed = item.Seed,
            DisplayedOptions = item.DisplayedOptions,
            Image = v.Image,
            SystemMessage = item.SystemMessage,
            UserMessage = item.UserMessage,
            Expected = item.Expected,
            CorrectOptionId = v.IsChoice ? v.CorrectOptionId : null,
            CorrectLetter = item.CorrectLetter,
            AcceptedAnswers = v.IsChoice ? null : v.AcceptedAnswers,
            StartedAt = DateTime.Now,
        };

        string? imageUrl = null;
        if (v.Image != null)
        {
            if (!vision)
            {
                rec.Status = ItemStatus.Skipped;
                rec.Reason = "no-vision";
                return rec;
            }
            var bytes = await File.ReadAllBytesAsync(ws.Resolve(v.Image), ct).ConfigureAwait(false);
            var mime = Path.GetExtension(v.Image).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            imageUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        var req = RequestBuilder.Build(def, item.SystemMessage, item.UserMessage, item.Seed, imageUrl);
        rec.Request = RequestBuilder.ForLog(req);

        var res = await client.ChatAsync(req, TimeSpan.FromSeconds(def.TimeoutSeconds), ct).ConfigureAwait(false);
        rec.ElapsedSeconds = res.ElapsedSeconds;
        rec.HttpStatus = res.HttpStatus;
        rec.Content = res.Content;
        rec.ReasoningContent = res.ReasoningContent;
        rec.FinishReason = res.FinishReason;
        rec.Usage = res.Usage;
        rec.Timings = res.Timings;
        rec.PromptTokens = res.PromptTokens;
        rec.CompletionTokens = res.CompletionTokens;

        if (!res.Success)
        {
            rec.Status = ItemStatus.Error;
            rec.Reason = res.TimedOut ? "timeout" : "http";
            rec.Error = res.Error;
            return rec;
        }

        var score = Scorer.Score(v, item.DisplayedOptions, res.Content, res.FinishReason);
        rec.Status = score.Status;
        rec.Reason = score.Reason;
        rec.ExtractedAnswer = score.ExtractedAnswer;
        rec.ChosenLetter = score.ChosenLetter;
        rec.ChosenOptionId = score.ChosenOptionId;
        rec.ThinkStripped = score.ThinkStripped;
        return rec;
    }

    private static string Shorten(string? s, int max)
    {
        var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
