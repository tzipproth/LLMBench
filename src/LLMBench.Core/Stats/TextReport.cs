using System.Globalization;
using System.Text;
using LLMBench.Core.Runs;

namespace LLMBench.Core.Stats;

/// <summary>Plain-text summary used by the CLI and the GUI.</summary>
public static class TextReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Pct(double? v) => v.HasValue ? (v.Value * 100).ToString("0.0", Inv) + " %" : "-";

    public static string HeaderLine(RunHeader? h, RunDefinition def)
    {
        var prompt = string.IsNullOrWhiteSpace(def.SystemPromptFile) ? "(none)" : Path.GetFileNameWithoutExtension(def.SystemPromptFile);
        var thinking = def.Thinking + (h?.ThinkingVerified switch { true => " (verified)", false => " (NOT verified)", _ => "" });
        var model = string.IsNullOrWhiteSpace(def.Model) ? h?.ModelAlias ?? "?" : def.Model;
        var variants = def.AllVariants ? "all" : string.Join(",", def.Variants);
        return $"Model: {model} | thinking: {thinking} | prompt: {prompt} | instruction: {def.AnswerInstruction} | variant: {variants}";
    }

    public static string Format(RunHeader? h, RunDefinition def, RunSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderLine(h, def));
        if (h != null)
            sb.AppendLine($"Server: {h.ModelPath ?? "?"} | ctx {h.ContextSize?.ToString(Inv) ?? "?"} | started {h.StartedAt:yyyy-MM-dd HH:mm:ss} | catalog {h.CatalogFile}");
        var state = s.Completed ? "completed" : s.Aborted ? $"ABORTED ({s.AbortReason})" : "incomplete";
        sb.AppendLine($"State: {state} | {s.Done}/{s.Planned} items | {s.DurationSeconds.ToString("0", Inv)} s | tokens in {s.PromptTokens} / out {s.CompletionTokens}");
        sb.AppendLine("Time: " + RunTiming.Line(s));
        if (s.Budget != null)
        {
            sb.AppendLine("Budget: " + BudgetLine(s.Budget));
            sb.AppendLine($"Net value: {Pct(s.Budget.NetScore)} (correct +1, wrong -1, wrong choice -1/(options-1), invalid/truncated 0)" +
                          (s.Budget.NetPerMinute is { } npm ? $" | {npm.ToString("0.0", Inv)} per minute (this machine)" : ""));
        }
        sb.AppendLine();
        sb.AppendLine($"Items: {s.Done}   correct {s.Correct}   incorrect {s.Incorrect}   invalid {s.Invalid}   truncated {s.Truncated}   error {s.Error} (excluded)   skipped {s.Skipped} (excluded)");
        var ci = s.CiLow.HasValue ? $"{(s.CiLow.Value * 100).ToString("0", Inv)}–{(s.CiHigh!.Value * 100).ToString("0", Inv)} %" : "-";
        if (s.CiLow.HasValue && s.CiQuestions > 0 && s.CiQuestions < s.Scored) ci += $" (over {s.CiQuestions} questions; repetitions are not extra questions)";
        var chance = s.ChanceLevel.HasValue ? Pct(s.ChanceLevel) + (s.FreeItems > 0 ? $" (choice items only; {s.FreeItems} free items)" : "") : "- (free answers only)";
        sb.AppendLine($"Accuracy: {Pct(s.Accuracy)} ({s.Correct}/{s.Scored})   95 % CI: {ci}   chance level: {chance}");
        if (s.Scored > 0 && (double)(s.Invalid + s.Truncated) / s.Scored >= 0.05)
            sb.AppendLine($"WARNING: invalid + truncated = {Pct((double)(s.Invalid + s.Truncated) / s.Scored)} - check settings before comparing models.");
        if (s.InvalidReasons.Count > 0)
            sb.AppendLine("Invalid reasons: " + string.Join(", ", s.InvalidReasons.Select(kv => $"{kv.Key} {kv.Value}")));
        if (s.Stability != null)
            sb.AppendLine($"Stability: {s.Stability.SolvedInAll}/{s.Stability.QuestionVariants} solved in all repetitions, {s.Stability.SolvedInNone} in none, mean solve rate {Pct(s.Stability.MeanSolveRate)}");

        if (s.ControlPairs is { } cp)
        {
            sb.AppendLine();
            sb.AppendLine($"Control pairs (trap variant vs. 'control', {cp.Pairs} pairs):");
            sb.AppendLine($"  both correct       (reads and understands)      {cp.BothCorrect.Count,4}  {Keys(cp.BothCorrect)}");
            sb.AppendLine($"  trap wrong only    (recognizes known puzzle)    {cp.RecognizesOnly.Count,4}  {Keys(cp.RecognizesOnly)}");
            sb.AppendLine($"  control wrong only (over-suspicious)            {cp.OverSuspicious.Count,4}  {Keys(cp.OverSuspicious)}");
            sb.AppendLine($"  both wrong         (too hard)                   {cp.BothWrong.Count,4}  {Keys(cp.BothWrong)}");
        }

        AppendGroups(sb, "By category", s.ByCategory);
        if (s.ByVariant.Count > 1) AppendGroups(sb, "By variant", s.ByVariant);
        if (s.ByTier.Count > 1) AppendGroups(sb, "By tier", s.ByTier);

        if (h?.Warnings.Count > 0)
        {
            sb.AppendLine();
            foreach (var w in h.Warnings) sb.AppendLine("Warning: " + w);
        }
        return sb.ToString();
    }

    /// <summary>"median 698 tokens / 11.8 s per answer | accuracy with at most 1000 tokens 62.3 %, ... | within 30 s ..."</summary>
    public static string BudgetLine(BudgetStat b)
    {
        var tok = string.Join(", ", BudgetStat.TokenLimits.Select((l, i) => $"{l} {Pct(b.AccuracyAtTokens[i])}"));
        var sec = string.Join(", ", BudgetStat.SecondLimits.Select((l, i) => $"{l} s {Pct(b.AccuracyWithinSeconds[i])}"));
        var perCorrect = b.TokensPerCorrect is { } t ? $", {t.ToString("0", Inv)} tokens per correct answer" : "";
        return $"median {b.MedianTokens.ToString("0", Inv)} tokens / {b.MedianSeconds.ToString("0.0", Inv)} s per answer{perCorrect} | " +
               $"accuracy with at most N tokens: {tok} | within: {sec} (this machine)";
    }

    private static string Keys(List<string> keys) =>
        keys.Count == 0 ? "" : keys.Count <= 8 ? string.Join(", ", keys) : string.Join(", ", keys.Take(8)) + $", … (+{keys.Count - 8})";

    private static void AppendGroups(StringBuilder sb, string title, List<GroupStat> groups)
    {
        if (groups.Count == 0) return;
        int w = Math.Max(title.Length, groups.Max(g => g.Key.Length)) + 2;
        sb.AppendLine();
        sb.AppendLine($"{title.PadRight(w)}{"n",5}{"correct",9}{"",9}");
        foreach (var g in groups)
            sb.AppendLine($"{g.Key.PadRight(w)}{g.N,5}{g.Correct,9}{Pct(g.Accuracy),9}");
    }
}
