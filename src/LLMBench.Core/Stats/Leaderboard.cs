using LLMBench.Core.Runs;

namespace LLMBench.Core.Stats;

public sealed class LeaderboardEntry
{
    public required string RunName { get; init; }
    public required RunDefinition Definition { get; init; }
    public required RunHeader Header { get; init; }
    public required RunSummary Summary { get; init; }
    public required RunState State { get; init; }
    public required string QuestionSetHash { get; init; }
    public required IReadOnlyList<ItemRecord> Items { get; init; }

    public string Model => string.IsNullOrWhiteSpace(Definition.Model) ? Header.ModelAlias ?? "?" : Definition.Model;
    public string ModelFile => Path.GetFileName(Header.ModelPath ?? "") is { Length: > 0 } f ? f : "?";
    public string SystemPrompt => string.IsNullOrWhiteSpace(Definition.SystemPromptFile) ? "(none)" : Path.GetFileNameWithoutExtension(Definition.SystemPromptFile);
    public string Thinking => Definition.Thinking + (Header.ThinkingVerified switch { true => " ✔", false => " ✘", _ => "" });

    /// <summary>invalid + truncated share of the scored items.</summary>
    public double? FormatFailureRate => Summary.Scored > 0 ? (double)(Summary.Invalid + Summary.Truncated) / Summary.Scored : null;

    /// <summary>Sum of the request times of all answers: pure model time, without warm-up, probe and pauses.</summary>
    public double ModelSeconds => Summary.RequestSeconds;

    /// <summary>Mean request time per answer in seconds.</summary>
    public double? SecondsPerAnswer => RunTiming.SecondsPerAnswer(Summary);

    /// <summary>Mean number of output tokens per answer (how much the model writes / reasons).</summary>
    public double? TokensPerAnswer => RunTiming.TokensPerAnswer(Summary);

    /// <summary>Output tokens per second of request time (speed of the model on this machine).</summary>
    public double? TokensPerSecond => RunTiming.TokensPerSecond(Summary);

    /// <summary>Text in the server notes (run option --notes) that marks a MoE model whose experts partly stay in system RAM.</summary>
    public const string ExpertsInRamNote = "experts in system RAM";

    /// <summary>The model did not fit completely into VRAM: a MoE model with experts kept in system RAM (see <see cref="ExpertsInRamNote"/>).</summary>
    public bool ExpertsInRam => Definition.ServerNotes.Contains(ExpertsInRamNote, StringComparison.OrdinalIgnoreCase);

    /// <summary>Share of the weights in system RAM in percent, from the server notes ("experts in system RAM (32 %)"), or null.</summary>
    public int? RamSharePercent =>
        ExpertsInRam && System.Text.RegularExpressions.Regex.Match(Definition.ServerNotes, @"(\d{1,3})\s*%") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>Concept 10.3: from 5 % invalid + truncated a run should not be used for model comparisons.</summary>
    public bool Usable => State == RunState.Completed && FormatFailureRate is < 0.05 && Header.ThinkingVerified != false;

    public string UsabilityNote =>
        State != RunState.Completed ? "incomplete"
        : FormatFailureRate is >= 0.05 ? "invalid+truncated ≥ 5 %"
        : Header.ThinkingVerified == false ? "thinking not verified"
        : "";

    /// <summary>
    /// No other shown run is at least as accurate and at most as long (median output tokens), and better in one of
    /// the two. Uses tokens, not seconds, so it does not depend on the hardware.
    /// </summary>
    public bool Pareto { get; set; }

    /// <summary>Score on the calibrated ability scale (null without a calibration for this question set).</summary>
    public Ability? Ability { get; set; }

    /// <summary>Position in the list sorted by accuracy (1, 2, 3, ...).</summary>
    public int Position { get; set; }

    /// <summary>1 + number of shown runs that are significantly better in the paired test (shared rank = not distinguishable).</summary>
    public int? Rank { get; set; }

    /// <summary>Shown runs that are significantly better than this one.</summary>
    public List<string> SignificantlyWorseThan { get; } = [];

    /// <summary>Shown runs that are significantly worse than this one.</summary>
    public List<string> SignificantlyBetterThan { get; } = [];
}

public sealed class LeaderboardGroup
{
    public required string QuestionSetHash { get; init; }
    public required int QuestionCount { get; init; }
    public required bool ExactIdentity { get; init; }
    public required List<string> CatalogFiles { get; init; }
    public List<LeaderboardEntry> Entries { get; } = [];
    /// <summary>Frozen item difficulties for this question set (calibrations/), or null.</summary>
    public Calibration? Calibration { get; set; }

    public string Title =>
        $"{string.Join(", ", CatalogFiles)} — {QuestionCount} question/variant pairs — {Entries.Count} run(s)" +
        (ExactIdentity ? "" : " (approx. identity)") + $"  [{QuestionSetHash}]";
}

/// <summary>
/// Groups all runs with results by the questions they ask (<see cref="QuestionSet"/>). Within a group every run
/// answered exactly the same questions, so all of them - also several runs of one model with different
/// parameters or system prompts - can be ranked against each other.
/// </summary>
public static class Leaderboard
{
    public static List<LeaderboardGroup> Build(Workspace ws)
    {
        var groups = new Dictionary<string, LeaderboardGroup>();
        foreach (var name in ws.Runs.ListNames())
        {
            if (!ws.Runs.HasResults(name)) continue;
            RunResults res;
            try { res = RunResults.Load(ws.Runs.ResultsPath(name)); }
            catch (IOException) { continue; }
            if (res.Header == null) continue;

            var id = QuestionSet.ForRun(ws, res.Header, res.Items);
            var state = res.Summary == null ? RunState.Incomplete : res.Summary.Aborted ? RunState.Aborted : RunState.Completed;
            if (!groups.TryGetValue(id.Hash, out var g))
            {
                g = new LeaderboardGroup { QuestionSetHash = id.Hash, QuestionCount = id.Count, ExactIdentity = id.Exact, CatalogFiles = [] };
                groups[id.Hash] = g;
            }
            if (!g.CatalogFiles.Contains(res.Header.CatalogFile)) g.CatalogFiles.Add(res.Header.CatalogFile);
            g.Entries.Add(new LeaderboardEntry
            {
                RunName = name,
                Definition = res.Header.Definition,
                Header = res.Header,
                Summary = SummaryBuilder.Rebuild(res),
                State = state,
                QuestionSetHash = id.Hash,
                Items = res.Items,
            });
        }
        // most recently used question set first (that is usually the one you want to see)
        var list = groups.Values.OrderByDescending(g => g.Entries.Max(e => e.Header.StartedAt)).ThenBy(g => g.Title).ToList();
        foreach (var g in list)
        {
            g.Calibration = Calibration.FindFor(ws, g.QuestionSetHash);
            if (g.Calibration != null)
                foreach (var e in g.Entries) e.Ability = Rasch.Estimate(g.Calibration, e.Items);
            AssignRanks(g.Entries);
        }
        return list;
    }

    /// <summary>
    /// Sorts by accuracy and assigns position and rank. All entries of a group answered the same questions,
    /// so every pair is compared with the paired per-question test (McNemar).
    /// Rank = 1 + number of entries that are significantly better (p &lt; 0.05).
    /// Note: many pairwise tests increase the chance of a single false "significant" result.
    /// </summary>
    public static List<LeaderboardEntry> AssignRanks(IList<LeaderboardEntry> entries)
    {
        var sorted = entries.OrderByDescending(e => e.Summary.Accuracy ?? -1).ThenBy(e => e.RunName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var e in sorted) { e.SignificantlyWorseThan.Clear(); e.SignificantlyBetterThan.Clear(); }
        for (int i = 0; i < sorted.Count; i++)
            for (int j = i + 1; j < sorted.Count; j++)
            {
                var t = RunComparer.Paired(sorted[i].Items, sorted[j].Items);
                if (t.ASignificantlyBetter) { sorted[j].SignificantlyWorseThan.Add(sorted[i].RunName); sorted[i].SignificantlyBetterThan.Add(sorted[j].RunName); }
                else if (t.BSignificantlyBetter) { sorted[i].SignificantlyWorseThan.Add(sorted[j].RunName); sorted[j].SignificantlyBetterThan.Add(sorted[i].RunName); }
            }
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].Position = i + 1;
            sorted[i].Rank = sorted[i].Summary.Accuracy == null ? null : 1 + sorted[i].SignificantlyWorseThan.Count;
        }
        MarkPareto(sorted);
        return sorted;
    }

    /// <summary>Sets <see cref="LeaderboardEntry.Pareto"/>: accuracy against median output tokens.</summary>
    public static void MarkPareto(IList<LeaderboardEntry> entries)
    {
        // only completed runs: an incomplete run with a few easy answers would look unbeatable
        var candidates = entries.Where(e => e.State == RunState.Completed && e.Summary.Accuracy != null && e.Summary.Budget != null).ToList();
        foreach (var e in entries)
        {
            e.Pareto = candidates.Contains(e) && !candidates.Any(o =>
            {
                if (ReferenceEquals(o, e)) return false;
                double ao = o.Summary.Accuracy!.Value, ae = e.Summary.Accuracy!.Value;
                double to = o.Summary.Budget!.MedianTokens, te = e.Summary.Budget!.MedianTokens;
                return ao >= ae && to <= te && (ao > ae || to < te);
            });
        }
    }

    /// <summary>Parameters that differ between the given runs (for the group header).</summary>
    public static List<string> VaryingParameters(IEnumerable<LeaderboardEntry> entries) =>
        ParameterDiff.Varying(entries.Select(e => (e.Definition, (RunHeader?)e.Header)));
}
