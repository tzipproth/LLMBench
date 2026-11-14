using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Stats;

public static class SummaryBuilder
{
    public static RunSummary Build(IReadOnlyList<ItemRecord> items, int planned)
    {
        var s = new RunSummary
        {
            Planned = planned,
            Done = items.Count,
            Correct = items.Count(i => i.Status == ItemStatus.Correct),
            Incorrect = items.Count(i => i.Status == ItemStatus.Incorrect),
            Invalid = items.Count(i => i.Status == ItemStatus.Invalid),
            Truncated = items.Count(i => i.Status == ItemStatus.Truncated),
            Error = items.Count(i => i.Status == ItemStatus.Error),
            Skipped = items.Count(i => i.Status == ItemStatus.Skipped),
            PromptTokens = items.Sum(i => (long)(i.PromptTokens ?? 0)),
            CompletionTokens = items.Sum(i => (long)(i.CompletionTokens ?? 0)),
            RequestSeconds = items.Sum(i => i.ElapsedSeconds),
        };

        var scored = items.Where(IsScored).ToList();
        s.Scored = scored.Count;
        if (s.Scored > 0)
        {
            s.Accuracy = (double)s.Correct / s.Scored;
            // Repetitions of a question are not independent questions (concept 11.2): the interval is computed
            // over the distinct question/variant pairs, each counting with its solve rate.
            var perQuestion = scored.GroupBy(i => i.Key)
                .Select(g => (double)g.Count(i => i.Status == ItemStatus.Correct) / g.Count())
                .ToList();
            s.CiQuestions = perQuestion.Count;
            (s.CiLow, s.CiHigh) = Wilson(perQuestion.Sum(), perQuestion.Count);
        }

        var choice = scored.Where(i => i.VariantType == "choice" && i.OptionCount > 0).ToList();
        s.ChoiceItems = choice.Count;
        s.FreeItems = scored.Count - choice.Count;
        if (choice.Count > 0) s.ChanceLevel = choice.Average(i => 1.0 / i.OptionCount);

        s.Budget = Budget(scored);

        s.InvalidReasons = items.Where(i => i.Status == ItemStatus.Invalid)
            .GroupBy(i => i.Reason ?? "?").ToDictionary(g => g.Key, g => g.Count());

        s.ByCategory = Group(scored, i => i.Category ?? "(none)");
        s.ByVariant = Group(scored, i => i.VariantId);
        s.ByTier = Group(scored, i => i.Tier?.ToString() ?? "(none)");

        if (scored.Select(i => i.Repetition).Distinct().Count() > 1)
        {
            var perKey = scored.GroupBy(i => i.Key).Select(g => (double)g.Count(i => i.Status == ItemStatus.Correct) / g.Count()).ToList();
            s.Stability = new StabilityStat
            {
                QuestionVariants = perKey.Count,
                SolvedInAll = perKey.Count(r => r >= 1.0),
                SolvedInNone = perKey.Count(r => r <= 0.0),
                MeanSolveRate = perKey.Average(),
            };
        }
        s.ControlPairs = BuildControlPairs(scored);
        return s;
    }

    /// <summary>
    /// Recomputes the statistics from the items (so older result files get newer statistics such as control pairs),
    /// keeping state and timing of the stored summary.
    /// </summary>
    public static RunSummary Rebuild(RunResults res)
    {
        var s = Build(res.Items, res.Header?.PlannedItems ?? res.Items.Count);
        if (res.Summary is { } old)
        {
            s.Completed = old.Completed;
            s.Aborted = old.Aborted;
            s.AbortReason = old.AbortReason;
            s.FinishedAt = old.FinishedAt;
            s.DurationSeconds = old.DurationSeconds;
        }
        return s;
    }

    private static string KindOf(ItemRecord i) =>
        i.VariantKind ?? (string.Equals(i.VariantId, "control", StringComparison.OrdinalIgnoreCase) ? "original" : "altered-legacy");

    /// <summary>
    /// Pairs every altered variant with the original of the same question and repetition (same format if possible).
    /// Old result files: the variant "control" is the original, the other variants of that question are the traps.
    /// </summary>
    private static ControlPairStat? BuildControlPairs(List<ItemRecord> scored)
    {
        var stat = new ControlPairStat();
        foreach (var g in scored.GroupBy(i => (i.QuestionId, i.Repetition)))
        {
            var originals = g.Where(i => KindOf(i) == "original").ToList();
            var traps = g.Where(i => KindOf(i) is "altered" || (KindOf(i) == "altered-legacy" && originals.Count > 0 && i.VariantKind == null)).ToList();
            if (originals.Count == 0 || traps.Count == 0) continue;
            foreach (var trap in traps)
            {
                var control = originals.FirstOrDefault(o => o.VariantType == trap.VariantType) ?? originals[0];
                bool c = control.Status == ItemStatus.Correct;
                bool t = trap.Status == ItemStatus.Correct;
                var key = trap.Key + (g.Key.Repetition > 0 ? $" r{g.Key.Repetition}" : "");
                var target = (t, c) switch
                {
                    (true, true) => stat.BothCorrect,
                    (false, true) => stat.RecognizesOnly,
                    (true, false) => stat.OverSuspicious,
                    _ => stat.BothWrong,
                };
                target.Add(key);
                stat.Pairs++;
            }
        }
        return stat.Pairs > 0 ? stat : null;
    }

    /// <summary>Median length and duration, accuracy under token and time limits (see <see cref="BudgetStat"/>).</summary>
    public static BudgetStat? Budget(IReadOnlyList<ItemRecord> scored)
    {
        if (scored.Count == 0) return null;
        double n = scored.Count;
        var correct = scored.Where(i => i.Status == ItemStatus.Correct).ToList();
        double net = correct.Count - scored.Where(i => i.Status == ItemStatus.Incorrect).Sum(WrongPenalty);
        double minutes = scored.Sum(i => i.ElapsedSeconds) / 60;
        return new BudgetStat
        {
            MedianTokens = Median(scored.Select(i => (double)(i.CompletionTokens ?? 0))),
            MedianSeconds = Median(scored.Select(i => i.ElapsedSeconds)),
            TokensPerCorrect = correct.Count > 0 ? scored.Sum(i => (double)(i.CompletionTokens ?? 0)) / correct.Count : null,
            NetScore = net / n,
            NetPerMinute = minutes > 0 ? net / minutes : null,
            AccuracyAtTokens = BudgetStat.TokenLimits.Select(l => correct.Count(i => (i.CompletionTokens ?? 0) <= l) / n).ToList(),
            AccuracyWithinSeconds = BudgetStat.SecondLimits.Select(l => correct.Count(i => i.ElapsedSeconds <= l) / n).ToList(),
        };
    }

    /// <summary>Cost of a wrong answer: 1 for a free answer, 1/(k-1) for a choice question with k options.</summary>
    public static double WrongPenalty(ItemRecord i) =>
        i.VariantType == "choice" && i.OptionCount > 1 ? 1.0 / (i.OptionCount - 1) : 1.0;

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    public static bool IsScored(ItemRecord i) =>
        i.Status is ItemStatus.Correct or ItemStatus.Incorrect or ItemStatus.Invalid or ItemStatus.Truncated;

    private static List<GroupStat> Group(List<ItemRecord> items, Func<ItemRecord, string> key) =>
        items.GroupBy(key)
            .Select(g => new GroupStat { Key = g.Key, N = g.Count(), Correct = g.Count(i => i.Status == ItemStatus.Correct) })
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Wilson score interval for a proportion (95 %, z = 1.96).</summary>
    public static (double Low, double High) Wilson(double successes, int n, double z = 1.96)
    {
        if (n == 0) return (0, 1);
        double p = successes / n;
        double z2 = z * z;
        double denom = 1 + z2 / n;
        double center = (p + z2 / (2 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }
}
