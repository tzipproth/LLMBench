using System.Globalization;
using System.Text;
using LLMBench.Core.Catalog;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Stats;

/// <summary>
/// Writes questions and per-model results of a leaderboard group as one Markdown document that another LLM
/// can read to propose new questions (catalog calibration, concept 3.2).
/// </summary>
public static class LlmExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(Workspace ws, LeaderboardGroup group, IReadOnlyList<LeaderboardEntry> entries)
    {
        if (entries.Count == 0) throw new BenchException("No runs selected for the export.");
        var runs = Leaderboard.AssignRanks(entries.ToList());
        var stats = QuestionStats.Build(runs);

        // canonical question texts from the catalog file (only if it is unchanged since the runs)
        var catalogFile = runs[0].Header.CatalogFile;
        QuestionCatalog? catalog = null;
        string? catalogNote = null;
        try
        {
            var c = CatalogLoader.Load(ws, catalogFile);
            if (c.IsValid && runs.All(r => r.Header.CatalogSha256 == c.Sha256)) catalog = c;
            else catalogNote = "The catalog file changed after the runs; question texts are taken from the result files.";
        }
        catch (BenchException ex) { catalogNote = ex.Message; }

        var sb = new StringBuilder();
        sb.AppendLine("# LLMBench – questions and model results");
        sb.AppendLine();
        sb.AppendLine($"Exported {DateTime.Now:yyyy-MM-dd HH:mm} from LLMBench {BenchRunner.Version}. Catalog `{catalogFile}`, " +
                      $"{group.QuestionCount} question/variant pairs (question set `{group.QuestionSetHash}`), {runs.Count} runs.");
        if (catalogNote != null) sb.AppendLine($"Note: {catalogNote}");
        sb.AppendLine();
        AppendAbout(sb);
        AppendRuns(sb, runs);
        AppendSummary(sb, stats, runs);
        AppendQuestions(sb, stats, runs, catalog);
        AppendTask(sb);
        return sb.ToString();
    }

    private static void AppendAbout(StringBuilder sb)
    {
        sb.AppendLine("## About the benchmark");
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
    }

    private static void AppendRuns(StringBuilder sb, List<LeaderboardEntry> runs)
    {
        sb.AppendLine("## Runs (models and settings)");
        sb.AppendLine();
        var tiers = runs.SelectMany(r => r.Summary.ByTier.Select(t => t.Key)).Distinct().OrderBy(t => t).ToList();
        sb.AppendLine("| Run | Model | Model file | Thinking | System prompt | Instruction | Temp | Accuracy | 95 % CI | " + string.Join(" | ", tiers.Select(t => $"Tier {t}")) + " | Tokens/answer | s/answer | Tokens/s | Model time |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", 13 + tiers.Count)));
        foreach (var r in runs)
        {
            var s = r.Summary;
            var ci = s.CiLow.HasValue ? $"{s.CiLow * 100:0}–{s.CiHigh * 100:0} %" : "";
            var tierCells = tiers.Select(t => s.ByTier.FirstOrDefault(g => g.Key == t) is { } g ? $"{TextReport.Pct(g.Accuracy)} ({g.Correct}/{g.N})" : "-");
            sb.AppendLine($"| {r.RunName} | {r.Model} | {r.ModelFile} | {r.Definition.Thinking} | {r.SystemPrompt} | {r.Definition.AnswerInstruction} | {r.Definition.Temperature?.ToString("0.##", Inv) ?? "default"} | " +
                          $"{TextReport.Pct(s.Accuracy)} | {ci} | {string.Join(" | ", tierCells)} | {r.TokensPerAnswer?.ToString("0", Inv) ?? "-"} | " +
                          $"{r.SecondsPerAnswer?.ToString("0.0", Inv) ?? "-"} | {r.TokensPerSecond?.ToString("0.0", Inv) ?? "-"} | {RunTiming.Duration(r.ModelSeconds)} |");
        }
        sb.AppendLine();
        sb.AppendLine("Accuracy by category:");
        sb.AppendLine();
        var cats = runs.SelectMany(r => r.Summary.ByCategory.Select(c => c.Key)).Distinct().OrderBy(c => c).ToList();
        sb.AppendLine("| Category | " + string.Join(" | ", runs.Select(r => r.RunName)) + " |");
        sb.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", runs.Count)));
        foreach (var c in cats)
            sb.AppendLine($"| {c} | " + string.Join(" | ", runs.Select(r => r.Summary.ByCategory.FirstOrDefault(g => g.Key == c) is { } g ? $"{TextReport.Pct(g.Accuracy)} ({g.Correct}/{g.N})" : "-")) + " |");
        sb.AppendLine();
    }

    private static void AppendSummary(StringBuilder sb, List<QuestionStatRow> stats, List<LeaderboardEntry> runs)
    {
        sb.AppendLine("## Calibration summary");
        sb.AppendLine();
        var all = stats.Where(s => s.Calibration == "all solve").Select(s => s.Key).ToList();
        var none = stats.Where(s => s.Calibration == "none solves").Select(s => s.Key).ToList();
        var sep = stats.Count(s => s.Calibration == "separates");
        sb.AppendLine($"- {sep} question/variant pairs separate the runs.");
        sb.AppendLine($"- Solved by all runs ({all.Count}): {(all.Count == 0 ? "-" : string.Join(", ", all))}");
        sb.AppendLine($"- Solved by no run ({none.Count}): {(none.Count == 0 ? "-" : string.Join(", ", none))}");
        if (runs.Any(r => r.Summary.ControlPairs != null))
        {
            sb.AppendLine("- Control pairs (trap variant vs. unchanged classic):");
            foreach (var r in runs.Where(r => r.Summary.ControlPairs != null))
            {
                var cp = r.Summary.ControlPairs!;
                sb.AppendLine($"  - {r.RunName}: {cp.Pairs} pairs, understands {cp.BothCorrect.Count}, only recognizes the known puzzle {cp.RecognizesOnly.Count}" +
                              (cp.RecognizesOnly.Count > 0 ? $" ({string.Join(", ", cp.RecognizesOnly)})" : "") +
                              $", over-suspicious {cp.OverSuspicious.Count}, both wrong {cp.BothWrong.Count}");
            }
        }
        sb.AppendLine();
    }

    private static void AppendQuestions(StringBuilder sb, List<QuestionStatRow> stats, List<LeaderboardEntry> runs, QuestionCatalog? catalog)
    {
        sb.AppendLine("## Questions with results");
        sb.AppendLine();
        sb.AppendLine("Sorted by category and id; each entry is one asked variant as resolved from base + variant. For multiple-choice variants the options are listed in catalog order; the runs saw them shuffled, the answers below are given as option text.");
        sb.AppendLine();
        foreach (var row in stats.OrderBy(s => s.Category, StringComparer.Ordinal).ThenBy(s => s.QuestionId, StringComparer.Ordinal).ThenBy(s => s.VariantId, StringComparer.Ordinal))
        {
            var q = catalog?.Questions.FirstOrDefault(x => x.Id == row.QuestionId);
            var v = q?.Variants.FirstOrDefault(x => x.Id == row.VariantId);
            var sample = runs.SelectMany(r => r.Items).FirstOrDefault(i => i.Key == row.Key);

            var kindFormat = v != null ? $"{v.Kind}, {(v.IsChoice ? "multiple choice" : "free answer")}, " : "";
            sb.AppendLine($"### {row.Key} — {kindFormat}tier {row.Tier?.ToString(Inv) ?? "-"}, {row.Category ?? "-"} — solved by {row.SolvedBy}/{row.Runs} ({row.Calibration})");
            sb.AppendLine();
            if (v != null)
            {
                sb.AppendLine(v.Question);
                sb.AppendLine();
                if (v.IsChoice)
                {
                    foreach (var o in v.Options) sb.AppendLine($"- {(o.Id == v.CorrectOptionId ? "**" + o.Text + "** (correct)" : o.Text)}");
                }
                else sb.AppendLine($"Accepted answers ({(v.IsNumber ? "number" : "text")}): {string.Join(" | ", v.AcceptedAnswers)}");
                if (v.Note != null) sb.AppendLine($"\nNote: {v.Note}");
            }
            else if (sample != null)
            {
                sb.AppendLine("Prompt as sent (incl. answer instruction):");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(sample.UserMessage);
                sb.AppendLine("```");
                sb.AppendLine($"Expected: {row.Expected}");
            }
            sb.AppendLine();
            foreach (var r in runs)
            {
                var items = r.Items.Where(i => i.Key == row.Key).ToList();
                if (items.Count == 0) { sb.AppendLine($"- {r.RunName}: not asked"); continue; }
                var parts = items.Select(i => $"{Mark(i)} {AnswerText(i)}{(i.Status is ItemStatus.Invalid or ItemStatus.Truncated ? $" ({i.Status.ToString().ToLowerInvariant()}: {i.Reason})" : "")}");
                sb.AppendLine($"- {r.RunName}: {string.Join("; ", parts)}");
            }
            sb.AppendLine();
        }
    }

    private static string Mark(ItemRecord i) => i.Status switch
    {
        ItemStatus.Correct => "✔",
        ItemStatus.Incorrect => "✘",
        ItemStatus.Skipped or ItemStatus.Error => "–",
        _ => "✘",
    };

    private static string AnswerText(ItemRecord i)
    {
        if (i.Status == ItemStatus.Error) return "error";
        if (i.ExtractedAnswer == null) return "no answer";
        if (i.ChosenOptionId != null && i.DisplayedOptions.FirstOrDefault(o => o.Id == i.ChosenOptionId) is { } o) return $"\"{o.Text}\"";
        return $"\"{i.ExtractedAnswer}\"";
    }

    private static void AppendTask(StringBuilder sb)
    {
        sb.AppendLine("## Lessons from earlier questions");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.Lessons);
        sb.AppendLine();
        sb.AppendLine("## Task for you");
        sb.AppendLine();
        sb.AppendLine(BenchDocs.Task());
    }
}
