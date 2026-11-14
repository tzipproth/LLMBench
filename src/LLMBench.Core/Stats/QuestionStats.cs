using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Stats;

public sealed class QuestionStatRow
{
    public required string QuestionId { get; init; }
    public required string VariantId { get; init; }
    public string? Category { get; init; }
    public int? Tier { get; init; }
    public string Expected { get; init; } = "";
    /// <summary>Solve rate per run name (0..1; with repetitions the share of correct repetitions). Missing = not asked/scored.</summary>
    public Dictionary<string, double> RateByRun { get; } = [];
    public int Runs => RateByRun.Count;
    public int SolvedBy => RateByRun.Values.Count(r => r >= 0.5);
    public double? MeanRate => RateByRun.Count > 0 ? RateByRun.Values.Average() : null;
    public string Key => $"{QuestionId}/{VariantId}";

    /// <summary>Concept 3.2 rule 6: questions that all or none of the runs solve do not separate models.</summary>
    public string Calibration => Runs < 2 ? ""
        : RateByRun.Values.All(r => r >= 1.0) ? "all solve"
        : RateByRun.Values.All(r => r <= 0.0) ? "none solves"
        : "separates";
}

/// <summary>Per-question solve rates across several runs (for calibrating the catalog).</summary>
public static class QuestionStats
{
    public static List<QuestionStatRow> Build(IEnumerable<LeaderboardEntry> entries)
    {
        var rows = new Dictionary<string, QuestionStatRow>();
        foreach (var e in entries)
        {
            foreach (var g in e.Items.Where(SummaryBuilder.IsScored).GroupBy(i => i.Key))
            {
                var first = g.First();
                if (!rows.TryGetValue(g.Key, out var row))
                {
                    row = new QuestionStatRow
                    {
                        QuestionId = first.QuestionId, VariantId = first.VariantId,
                        Category = first.Category, Tier = first.Tier, Expected = ExpectedText(first),
                    };
                    rows[g.Key] = row;
                }
                row.RateByRun[e.RunName] = (double)g.Count(i => i.Status == ItemStatus.Correct) / g.Count();
            }
        }
        return rows.Values
            .OrderBy(r => r.MeanRate ?? 1)
            .ThenBy(r => r.QuestionId, StringComparer.Ordinal)
            .ThenBy(r => r.VariantId, StringComparer.Ordinal)
            .ToList();
    }

    // the expected answer without the shuffled letter
    private static string ExpectedText(ItemRecord i) =>
        i.VariantType == "choice"
            ? i.DisplayedOptions.FirstOrDefault(o => o.Id == i.CorrectOptionId)?.Text ?? i.Expected
            : i.Expected;
}
