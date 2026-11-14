using System.Text;
using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Stats;

namespace LLMBench.Gui;

/// <summary>One row of the results grid: planned item, optionally with its result.</summary>
internal sealed class RowData
{
    public int Index { get; init; }
    public string QuestionId { get; init; } = "";
    public string VariantId { get; init; } = "";
    public int Repetition { get; init; }
    public string Expected { get; init; } = "";
    public string? SystemMessage { get; init; }
    public string UserMessage { get; init; } = "";
    public ItemRecord? Record { get; set; }
}

/// <summary>Everything the results view shows for one run (live or loaded from disk).</summary>
internal sealed class RunViewData
{
    public string Name { get; init; } = "";
    public RunDefinition Definition { get; init; } = new();
    public RunHeader? Header { get; set; }
    public RunSummary? Summary { get; set; }
    public List<RowData> Rows { get; } = [];
    public StringBuilder Log { get; } = new();
    public bool IsLive { get; set; }
    public bool IsPlanOnly { get; set; }
    public int? CurrentIndex { get; set; }
    public DateTime CurrentStarted { get; set; }
    public DateTime? RunStarted { get; set; }

    public static RunViewData FromPlan(RunDefinition def, RunPlan plan, bool live)
    {
        var d = new RunViewData { Name = def.Name, Definition = def, IsLive = live, IsPlanOnly = !live };
        foreach (var it in plan.Items)
            d.Rows.Add(new RowData
            {
                Index = it.Index, QuestionId = it.Question.Id, VariantId = it.Variant.Id, Repetition = it.Repetition,
                Expected = it.Expected, SystemMessage = it.SystemMessage, UserMessage = it.UserMessage,
            });
        foreach (var s in plan.Skipped) d.Log.AppendLine($"skipped {s.QuestionId}/{s.VariantId}: {s.Reason}");
        return d;
    }

    public static RunViewData FromResults(RunDefinition def, RunResults res)
    {
        var d = new RunViewData
        {
            Name = def.Name, Definition = def, Header = res.Header, RunStarted = res.Header?.StartedAt,
            // recomputed so that older result files also show newer statistics (e.g. control pairs)
            Summary = res.Header != null ? SummaryBuilder.Rebuild(res) : null,
        };
        foreach (var r in res.Items)
            d.Rows.Add(new RowData
            {
                Index = r.Index, QuestionId = r.QuestionId, VariantId = r.VariantId, Repetition = r.Repetition,
                Expected = r.Expected, SystemMessage = r.SystemMessage, UserMessage = r.UserMessage, Record = r,
            });
        if (res.Header != null)
        {
            d.Log.AppendLine($"Run {res.Header.RunId} started {res.Header.StartedAt:yyyy-MM-dd HH:mm:ss} on {res.Header.MachineName}, LLMBench {res.Header.LlmBenchVersion}");
            if (res.Header.Warmup != null) d.Log.AppendLine($"Warm-up: {(res.Header.Warmup.Ok ? "ok" : "failed")} {res.Header.Warmup.ElapsedSeconds:0.0} s: {res.Header.Warmup.Content?.Trim()}");
            foreach (var w in res.Header.Warnings) d.Log.AppendLine("WARNING: " + w);
            foreach (var s in res.Header.SkippedVariants) d.Log.AppendLine($"skipped {s.QuestionId}/{s.VariantId}: {s.Reason}");
        }
        if (res.Summary == null && res.Header != null)
            d.Log.AppendLine($"Run is incomplete: {res.Items.Count} of {res.Header.PlannedItems} items written, no summary line.");
        if (res.Header?.ThinkingProbe is { } p)
            d.Log.AppendLine($"Thinking probe: {(p.Verified ? "verified" : "NOT verified")} - {p.Note}");
        foreach (var e in res.ReadErrors) d.Log.AppendLine("read error: " + e);
        return d;
    }

    public List<ItemRecord> Records => Rows.Where(r => r.Record != null).Select(r => r.Record!).ToList();

    public RunSummary CurrentSummary() => Summary ?? SummaryBuilder.Build(Records, Rows.Count);
}
