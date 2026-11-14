using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Heatmap;

/// <summary>Result of one item as a colour class.</summary>
public enum CellKind { Missing, CorrectFast, Correct, CorrectSlow, Incorrect, Truncated, Invalid, Error }

public sealed record Column(string Key, string QuestionId, string VariantId, int Tier, int CatalogIndex, string Question, double SolveRate);

public sealed class HeatmapOptions
{
    /// <summary>"seconds" (time on this machine) or "tokens" (hardware-independent) decides fast / slow.</summary>
    public string SpeedBy { get; set; } = "seconds";
    public double Fast { get; set; } = 10;
    public double Slow { get; set; } = 60;
    /// <summary>"difficulty" = per tier from easy to hard (solve rate over the shown runs), "catalog" = catalog order.</summary>
    public string Order { get; set; } = "difficulty";
}

/// <summary>Rows = runs (best first), columns = question/variant/repetition, cells = item results.</summary>
public sealed class HeatmapData
{
    public required LeaderboardGroup Group { get; init; }
    public required List<LeaderboardEntry> Rows { get; init; }
    public required List<Column> Columns { get; init; }
    /// <summary>[row][column], null = not answered in this run (incomplete run).</summary>
    public required ItemRecord?[][] Cells { get; init; }
    public required HeatmapOptions Options { get; init; }

    public static string ItemKey(ItemRecord i) => $"{i.QuestionId}/{i.VariantId}#{i.Repetition}";

    public static HeatmapData Build(LeaderboardGroup group, List<LeaderboardEntry> entries, HeatmapOptions opt)
    {
        var rows = Leaderboard.AssignRanks(entries);
        var maps = rows.Select(e => e.Items.GroupBy(ItemKey).ToDictionary(g => g.Key, g => g.Last())).ToList();

        // all keys, with the catalog position from the most complete run
        var richest = rows.OrderByDescending(e => e.Items.Count).First();
        var catalogIndex = richest.Items.Select((it, i) => (ItemKey(it), i)).GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().i);
        var keys = maps.SelectMany(m => m.Keys).Distinct().ToList();

        var columns = keys.Select(k =>
        {
            var sample = maps.Select(m => m.GetValueOrDefault(k)).First(x => x != null)!;
            var answered = maps.Select(m => m.GetValueOrDefault(k)).Where(x => x != null && SummaryBuilder.IsScored(x)).ToList();
            double rate = answered.Count == 0 ? 0 : answered.Count(x => x!.Status == ItemStatus.Correct) / (double)answered.Count;
            return new Column(k, sample.QuestionId, sample.VariantId, sample.Tier ?? 0,
                catalogIndex.GetValueOrDefault(k, int.MaxValue), sample.UserMessage ?? "", rate);
        });
        columns = opt.Order == "catalog"
            ? columns.OrderBy(c => c.CatalogIndex)
            : columns.OrderBy(c => c.Tier).ThenByDescending(c => c.SolveRate).ThenBy(c => c.CatalogIndex);
        var cols = columns.ToList();

        var cells = maps.Select(m => cols.Select(c => m.GetValueOrDefault(c.Key)).ToArray()).ToArray();
        return new HeatmapData { Group = group, Rows = rows, Columns = cols, Cells = cells, Options = opt };
    }

    public CellKind Kind(ItemRecord? i)
    {
        if (i == null) return CellKind.Missing;
        switch (i.Status)
        {
            case ItemStatus.Correct:
                double v = Options.SpeedBy == "tokens" ? i.CompletionTokens ?? 0 : i.ElapsedSeconds;
                return v <= Options.Fast ? CellKind.CorrectFast : v > Options.Slow ? CellKind.CorrectSlow : CellKind.Correct;
            case ItemStatus.Incorrect: return CellKind.Incorrect;
            case ItemStatus.Truncated: return CellKind.Truncated;
            case ItemStatus.Invalid: return CellKind.Invalid;
            case ItemStatus.Error: return CellKind.Error;
            default: return CellKind.Missing;
        }
    }

    public static uint Color(CellKind k) => k switch
    {
        CellKind.CorrectFast => 0x1E8C32,  // dark green
        CellKind.Correct => 0x3CD250,      // green
        CellKind.CorrectSlow => 0xB4F0B4,  // light green
        CellKind.Incorrect => 0xE62828,    // red
        CellKind.Truncated => 0xFF8C00,    // orange: ran into max_tokens (loop / too long)
        CellKind.Invalid => 0xB450FF,      // violet: no usable final answer (format)
        CellKind.Error => 0x8C8C8C,        // grey: request failed
        _ => 0x323232,                     // dark grey: not answered yet (incomplete run)
    };

    public string Legend(CellKind k)
    {
        var unit = Options.SpeedBy == "tokens" ? " tokens" : " s";
        return k switch
        {
            CellKind.CorrectFast => $"correct, fast (≤ {Options.Fast}{unit})",
            CellKind.Correct => "correct",
            CellKind.CorrectSlow => $"correct, slow (> {Options.Slow}{unit})",
            CellKind.Incorrect => "incorrect",
            CellKind.Truncated => "truncated (max_tokens reached)",
            CellKind.Invalid => "invalid (no usable final answer)",
            CellKind.Error => "error (request failed)",
            _ => "not answered (run incomplete)",
        };
    }
}
