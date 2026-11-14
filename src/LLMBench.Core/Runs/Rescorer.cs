using LLMBench.Core.Catalog;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Core.Runs;

public sealed record RescoreChange(string Key, int Repetition, ItemStatus OldStatus, string? OldReason, ItemStatus NewStatus, string? NewReason, string? Answer);

public sealed class RescoreResult
{
    public required string RunName { get; init; }
    public List<RescoreChange> Changes { get; } = [];
    public RunSummary? OldSummary { get; set; }
    public RunSummary? NewSummary { get; set; }
    public string? BackupFile { get; set; }
}

/// <summary>
/// Scores the stored replies of a finished run again with the current scoring rules. No model is needed: every
/// result line contains the question as asked (options, correct option, accepted answers) and the full reply.
/// The question data is taken from the result file, never from the (possibly changed) catalog.
/// </summary>
public static class Rescorer
{
    public static RescoreResult Rescore(Workspace ws, string runName, bool dryRun)
    {
        var path = ws.Runs.ResultsPath(runName);
        var res = RunResults.Load(path);
        if (res.Header == null) throw new BenchException($"Run '{runName}' has no results.");
        if (res.Summary == null) throw new BenchException($"Run '{runName}' is incomplete or still running; only finished runs can be rescored.");
        if (res.ReadErrors.Count > 0) throw new BenchException($"Run '{runName}': results file has read errors ({res.ReadErrors[0]}).");

        var result = new RescoreResult { RunName = runName, OldSummary = res.Summary };
        foreach (var item in res.Items)
        {
            if (item.Status is ItemStatus.Error or ItemStatus.Skipped) continue;
            var score = Scorer.Score(VariantOf(item), item.DisplayedOptions, item.Content, item.FinishReason);
            if (score.Status != item.Status || score.Reason != item.Reason || score.ExtractedAnswer != item.ExtractedAnswer || score.ChosenOptionId != item.ChosenOptionId)
            {
                if (score.Status != item.Status || score.Reason != item.Reason)
                    result.Changes.Add(new RescoreChange(item.Key, item.Repetition, item.Status, item.Reason, score.Status, score.Reason, score.ExtractedAnswer));
                item.Status = score.Status;
                item.Reason = score.Reason;
                item.ExtractedAnswer = score.ExtractedAnswer;
                item.ChosenLetter = score.ChosenLetter;
                item.ChosenOptionId = score.ChosenOptionId;
                item.ThinkStripped = score.ThinkStripped;
            }
        }

        var summary = SummaryBuilder.Build(res.Items, res.Header.PlannedItems);
        summary.Completed = res.Summary.Completed;
        summary.Aborted = res.Summary.Aborted;
        summary.AbortReason = res.Summary.AbortReason;
        summary.FinishedAt = res.Summary.FinishedAt;
        summary.DurationSeconds = res.Summary.DurationSeconds;
        result.NewSummary = summary;
        if (dryRun || result.Changes.Count == 0) return result;

        // keep the original file, then write the rescored one
        var backup = Path.Combine(Path.GetDirectoryName(path)!, $"results.before-rescore-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        File.Copy(path, backup);
        result.BackupFile = backup;
        res.Header.Rescored.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} LLMBench {BenchRunner.Version}: {result.Changes.Count} item(s) changed");
        var tmp = path + ".tmp";
        using (var w = new ResultWriter(tmp))
        {
            w.Write(res.Header);
            foreach (var item in res.Items) w.Write(item);
            w.Write(summary);
        }
        File.Move(tmp, path, overwrite: true);
        return result;
    }

    /// <summary>The question variant as it was asked, rebuilt from the stored result line.</summary>
    public static QuestionVariant VariantOf(ItemRecord i) => new()
    {
        Id = i.VariantId,
        Kind = i.VariantKind ?? "original",
        Type = i.VariantType,
        AnswerType = i.AnswerType,
        Question = i.UserMessage,
        Options = i.DisplayedOptions.Select(o => new AnswerOption { Id = o.Id, Text = o.Text }).ToList(),
        CorrectOptionId = i.CorrectOptionId,
        AcceptedAnswers = i.AcceptedAnswers ?? [],
        Tier = i.Tier,
    };
}
