using System.Globalization;
using System.Text;
using LLMBench.Core.Catalog;

namespace LLMBench.Core.Stats;

/// <summary>
/// Short Markdown briefing that accompanies the CSV export: what the benchmark is, the catalog format,
/// the scoring rules, a computed results summary and the task for an LLM that proposes new questions.
/// </summary>
public static class LlmBriefing
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(IReadOnlyList<LeaderboardEntry> runs, QuestionCatalog? catalog, string csvFile, string legendFile)
    {
        var catalogFile = runs.Count > 0 ? runs[0].Header.CatalogFile : catalog?.RelativePath ?? "?";
        var sb = new StringBuilder();
        sb.AppendLine("# LLMBench – briefing for proposing new questions");
        sb.AppendLine();
        sb.AppendLine("You receive four files:");
        sb.AppendLine();
        sb.AppendLine($"- `{Path.GetFileName(catalogFile)}` – the question catalog (format below)");
        sb.AppendLine($"- `{csvFile}` – one row per asked variant with the answer and evaluation of every model, the question text last");
        sb.AppendLine($"- `{legendFile}` – which column number is which model, and the meaning of the columns and evaluation values");
        sb.AppendLine("- this briefing");
        sb.AppendLine();
        sb.AppendLine("## Purpose");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.Purpose);
        sb.AppendLine();
        sb.AppendLine("## Catalog format");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.CatalogFormat);
        sb.AppendLine();
        sb.AppendLine("## How answers are scored");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.ScoringRules);
        sb.AppendLine();
        AppendResults(sb, runs, catalog);
        sb.AppendLine("## Lessons from earlier questions");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.Lessons);
        sb.AppendLine();
        sb.AppendLine("## Your task");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.Task());
        return sb.ToString();
    }

    public static void AppendResults(StringBuilder sb, IReadOnlyList<LeaderboardEntry> runs, QuestionCatalog? catalog)
    {
        sb.AppendLine("## Results in brief");
        sb.AppendLine();
        if (runs.Count == 0) { sb.AppendLine("(no runs)"); sb.AppendLine(); return; }
        var r0 = runs[0].Definition;
        sb.AppendLine($"All runs used the same {(catalog != null ? $"{catalog.Questions.Count} questions / {catalog.VariantCount} variants" : "questions")} " +
                      $"and the same settings (system prompt `{runs[0].SystemPrompt}`, instruction `{r0.AnswerInstruction}`, temperature " +
                      $"{r0.Temperature?.ToString("0.##", Inv) ?? "default"}, thinking `{r0.Thinking}`); the column number in the CSV is the model number.");
        sb.AppendLine();

        var tiers = runs.SelectMany(r => r.Summary.ByTier.Select(t => t.Key)).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        sb.AppendLine("| # | Model | Accuracy | " + string.Join(" | ", tiers.Select(t => $"Tier {t}")) + " | Tokens/answer | s/answer | Model time | Weakest categories |");
        sb.AppendLine("|---|---|---|" + string.Concat(tiers.Select(_ => "---|")) + "---|---|---|---|");
        for (int i = 0; i < runs.Count; i++)
        {
            var s = runs[i].Summary;
            var weakest = s.ByCategory.Where(c => c.N >= 3 && c.Accuracy < 1).OrderBy(c => c.Accuracy).Take(3)
                .Select(c => $"{c.Key} {TextReport.Pct(c.Accuracy)}");
            sb.AppendLine($"| {i + 1} | {runs[i].Model} | {TextReport.Pct(s.Accuracy)} | " +
                          string.Join(" | ", tiers.Select(t => TextReport.Pct(s.ByTier.FirstOrDefault(g => g.Key == t)?.Accuracy))) +
                          $" | {runs[i].TokensPerAnswer?.ToString("0", Inv) ?? "-"} | {runs[i].SecondsPerAnswer?.ToString("0.0", Inv) ?? "-"} | {RunTiming.Duration(runs[i].ModelSeconds)}" +
                          $" | {(weakest.Any() ? string.Join(", ", weakest) : "-")} |");
        }
        sb.AppendLine();

        var stats = QuestionStats.Build(runs);
        var all = stats.Count(s => s.Calibration == "all solve");
        var none = stats.Count(s => s.Calibration == "none solves");
        var sep = stats.Count(s => s.Calibration == "separates");
        sb.AppendLine($"- {sep} variants separate the models, {all} are solved by **all** models (no information), {none} by none.");
        var strongest = runs[0];
        var hardest = stats.Where(s => s.RateByRun.TryGetValue(strongest.RunName, out var v) && v < 1).Select(s => s.Key).ToList();
        sb.AppendLine($"- The strongest model ({strongest.Model}) fails only: {(hardest.Count == 0 ? "nothing" : string.Join(", ", hardest))}. " +
                      "New hard questions should be at least as difficult as these.");
        var cps = runs.Where(r => r.Summary.ControlPairs != null).ToList();
        if (cps.Count > 0)
            sb.AppendLine("- Control pairs (altered wrong, original right = only recognizes the known puzzle): " +
                          string.Join("; ", cps.Select(r => $"model {runs.ToList().IndexOf(r) + 1}: {r.Summary.ControlPairs!.RecognizesOnly.Count} of {r.Summary.ControlPairs.Pairs}")) + ".");
        sb.AppendLine();
    }
}
