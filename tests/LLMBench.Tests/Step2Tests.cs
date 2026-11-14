using LLMBench.Core.Catalog;
using LLMBench.Core.Prompting;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Tests;

public class McNemarTests
{
    [Fact]
    public void No_discordant_pairs_gives_one() => Assert.Equal(1.0, RunComparer.McNemarExact(0, 0));

    [Fact]
    public void Nine_vs_two_is_borderline()
    {
        // binomial(11, 0.5): P(X<=2) = 67/2048, two-sided 0.0654
        Assert.Equal(0.0654, RunComparer.McNemarExact(9, 2), 3);
    }

    [Fact]
    public void Five_vs_four_is_noise() => Assert.Equal(1.0, RunComparer.McNemarExact(5, 4), 6);

    [Fact]
    public void Ten_vs_zero_is_significant() => Assert.True(RunComparer.McNemarExact(10, 0) < 0.01);

    [Fact]
    public void Symmetric() => Assert.Equal(RunComparer.McNemarExact(3, 12), RunComparer.McNemarExact(12, 3), 12);
}

public class ControlPairTests
{
    private static ItemRecord Item(string q, string v, ItemStatus s, int rep = 0) =>
        new() { QuestionId = q, VariantId = v, Status = s, Repetition = rep, VariantType = "choice" };

    [Fact]
    public void Classifies_pairs()
    {
        var items = new List<ItemRecord>
        {
            Item("A", "mcq", ItemStatus.Correct), Item("A", "control", ItemStatus.Correct),
            Item("B", "mcq", ItemStatus.Incorrect), Item("B", "control", ItemStatus.Correct),
            Item("C", "mcq", ItemStatus.Correct), Item("C", "control", ItemStatus.Invalid),
            Item("D", "mcq", ItemStatus.Incorrect), Item("D", "control", ItemStatus.Truncated),
            Item("E", "mcq", ItemStatus.Correct), // no control -> no pair
        };
        var cp = SummaryBuilder.Build(items, items.Count).ControlPairs!;
        Assert.Equal(4, cp.Pairs);
        Assert.Equal(["A/mcq"], cp.BothCorrect);
        Assert.Equal(["B/mcq"], cp.RecognizesOnly);
        Assert.Equal(["C/mcq"], cp.OverSuspicious);
        Assert.Equal(["D/mcq"], cp.BothWrong);
    }

    [Fact]
    public void Pairs_per_repetition_and_ignores_errors()
    {
        var items = new List<ItemRecord>
        {
            Item("A", "mcq", ItemStatus.Correct, 0), Item("A", "control", ItemStatus.Correct, 0),
            Item("A", "mcq", ItemStatus.Incorrect, 1), Item("A", "control", ItemStatus.Correct, 1),
            Item("A", "mcq", ItemStatus.Correct, 2), Item("A", "control", ItemStatus.Error, 2),
        };
        var cp = SummaryBuilder.Build(items, items.Count).ControlPairs!;
        Assert.Equal(2, cp.Pairs);
        Assert.Equal(["A/mcq r1"], cp.RecognizesOnly);
    }

    [Fact]
    public void No_control_variant_gives_null() =>
        Assert.Null(SummaryBuilder.Build([Item("A", "mcq", ItemStatus.Correct)], 1).ControlPairs);
}

public class QuestionSetTests
{
    private static (CatalogQuestion, QuestionVariant) Pair(string id, string text, string correct = "a")
    {
        var v = new QuestionVariant
        {
            Id = "mcq", Type = "choice", Question = text, CorrectOptionId = correct,
            Options = [new() { Id = "a", Text = "x" }, new() { Id = "b", Text = "y" }],
        };
        return (new CatalogQuestion { Id = id, Variants = [v] }, v);
    }

    [Fact]
    public void Order_independent()
    {
        var h1 = QuestionSet.Compute([Pair("Q1", "one"), Pair("Q2", "two")]);
        var h2 = QuestionSet.Compute([Pair("Q2", "two"), Pair("Q1", "one")]);
        Assert.Equal(h1.Hash, h2.Hash);
        Assert.Equal(2, h1.Count);
    }

    [Fact]
    public void Text_change_changes_identity() =>
        Assert.NotEqual(QuestionSet.Compute([Pair("Q1", "one")]).Hash, QuestionSet.Compute([Pair("Q1", "one!")]).Hash);

    [Fact]
    public void Solution_change_changes_identity() =>
        Assert.NotEqual(QuestionSet.Compute([Pair("Q1", "one", "a")]).Hash, QuestionSet.Compute([Pair("Q1", "one", "b")]).Hash);
}

public class ParameterDiffTests
{
    [Fact]
    public void Finds_differences_and_ignores_name()
    {
        var a = new RunDefinition { Name = "a", Temperature = 0 };
        var b = new RunDefinition { Name = "b", Temperature = 1, SystemPromptFile = "prompts/careful.txt" };
        var d = ParameterDiff.Diff(a, null, b, null);
        Assert.Equal(["systemPromptFile", "temperature"], d.Select(x => x.Name));
    }

    [Fact]
    public void Unused_thinking_fragment_is_ignored()
    {
        var a = new RunDefinition { Thinking = "off" };
        var b = new RunDefinition { Thinking = "off" };
        b.ThinkingOn = new() { ["reasoning_effort"] = "high" };
        Assert.Empty(ParameterDiff.Diff(a, null, b, null));
    }

    [Fact]
    public void Standard_fragments_do_not_count_as_difference()
    {
        var a = new RunDefinition { Thinking = "on" };
        var b = new RunDefinition { Thinking = "off" };
        Assert.Equal(["thinking"], ParameterDiff.Diff(a, null, b, null).Select(x => x.Name));
        b.ThinkingOff = new() { ["reasoning_effort"] = "none" };
        Assert.Equal(["thinking", "thinkingFragment"], ParameterDiff.Diff(a, null, b, null).Select(x => x.Name));
    }

    [Fact]
    public void Ranking_ignores_question_filters()
    {
        var a = new RunDefinition { Limit = 0 };
        var b = new RunDefinition { Limit = 5, ServerUrl = "http://other:1" };
        Assert.Empty(ParameterDiff.Varying([(a, null), (b, null)]));
    }
}

public class LeaderboardRankTests
{
    private static ItemRecord Item(int q, bool ok, int rep = 0) =>
        new() { QuestionId = $"Q{q}", VariantId = "mcq", Repetition = rep, Status = ok ? ItemStatus.Correct : ItemStatus.Incorrect, VariantType = "choice" };

    /// <summary>Run that solves the first <paramref name="solved"/> of n questions.</summary>
    private static LeaderboardEntry Entry(string name, int solved, int n)
    {
        var items = Enumerable.Range(0, n).Select(q => Item(q, q < solved)).ToList();
        return new LeaderboardEntry
        {
            RunName = name, Definition = new RunDefinition(), Header = new RunHeader(), State = RunState.Completed,
            QuestionSetHash = "x", Items = items, Summary = SummaryBuilder.Build(items, n),
        };
    }

    [Fact]
    public void Five_to_zero_is_not_proven_seven_to_zero_is()
    {
        var a = Entry("a", 21, 21);
        var b = Entry("b", 16, 21); // 5 discordant questions, all for a: p = 0.0625
        var c = Entry("c", 14, 21); // 7 discordant questions vs a: p = 0.016
        var sorted = Leaderboard.AssignRanks([c, b, a]);
        Assert.Equal(["a", "b", "c"], sorted.Select(e => e.RunName));
        Assert.Equal([1, 2, 3], sorted.Select(e => e.Position));
        Assert.Equal(1, a.Rank);
        Assert.Equal(1, b.Rank);
        Assert.Equal(2, c.Rank);
        Assert.Equal(["a"], c.SignificantlyWorseThan);
        Assert.Equal(["c"], a.SignificantlyBetterThan);
    }

    [Fact]
    public void Repetitions_count_each_question_once()
    {
        // a solves all 6 questions 3 times, b solves none: 6 questions for a, p = 0.031
        var ia = Enumerable.Range(0, 3).SelectMany(r => Enumerable.Range(0, 6).Select(q => Item(q, true, r))).ToList();
        var ib = Enumerable.Range(0, 3).SelectMany(r => Enumerable.Range(0, 6).Select(q => Item(q, false, r))).ToList();
        var t = RunComparer.Paired(ia, ib);
        Assert.Equal(6, t.ABetter);
        Assert.Equal(0.03125, t.PValue, 6);
    }
}

public class RepetitionCiTests
{
    private static ItemRecord Item(string q, int rep, bool ok) =>
        new() { QuestionId = q, VariantId = "mcq", Repetition = rep, Status = ok ? ItemStatus.Correct : ItemStatus.Incorrect, VariantType = "choice" };

    [Fact]
    public void Repetitions_do_not_narrow_the_interval()
    {
        // 21 questions, all correct, asked once vs. three times: same interval
        var once = Enumerable.Range(0, 21).Select(i => Item($"Q{i}", 0, true)).ToList();
        var thrice = Enumerable.Range(0, 3).SelectMany(r => Enumerable.Range(0, 21).Select(i => Item($"Q{i}", r, true))).ToList();
        var a = SummaryBuilder.Build(once, 21);
        var b = SummaryBuilder.Build(thrice, 63);
        Assert.Equal(a.CiLow!.Value, b.CiLow!.Value, 9);
        Assert.Equal(21, b.CiQuestions);
        Assert.Equal(63, b.Scored);
    }

    [Fact]
    public void Partial_solve_rates_count_fractionally()
    {
        var items = new List<ItemRecord> { Item("A", 0, true), Item("A", 1, false), Item("B", 0, true), Item("B", 1, true) };
        var s = SummaryBuilder.Build(items, 4);
        Assert.Equal(0.75, s.Accuracy!.Value, 9);
        var (lo, hi) = SummaryBuilder.Wilson(1.5, 2);
        Assert.Equal(lo, s.CiLow!.Value, 9);
        Assert.Equal(hi, s.CiHigh!.Value, 9);
    }
}

public class QuestionStatsTests
{
    private static LeaderboardEntry Run(string name, params (string q, bool ok)[] answers)
    {
        var items = answers.Select(a => new ItemRecord
        {
            QuestionId = a.q, VariantId = "mcq", VariantType = "free", Expected = "x",
            Status = a.ok ? ItemStatus.Correct : ItemStatus.Incorrect,
        }).ToList();
        return new LeaderboardEntry
        {
            RunName = name, Definition = new RunDefinition(), Header = new RunHeader(), State = RunState.Completed,
            QuestionSetHash = "x", Items = items, Summary = SummaryBuilder.Build(items, items.Count),
        };
    }

    [Fact]
    public void Classifies_questions_for_calibration()
    {
        var rows = QuestionStats.Build([
            Run("a", ("easy", true), ("hard", false), ("mid", true)),
            Run("b", ("easy", true), ("hard", false), ("mid", false)),
        ]);
        Assert.Equal("none solves", rows.Single(r => r.QuestionId == "hard").Calibration);
        Assert.Equal("all solve", rows.Single(r => r.QuestionId == "easy").Calibration);
        Assert.Equal("separates", rows.Single(r => r.QuestionId == "mid").Calibration);
        Assert.Equal("hard", rows[0].QuestionId); // hardest first
        Assert.Equal(0.5, rows.Single(r => r.QuestionId == "mid").MeanRate!.Value, 9);
    }
}

public class BudgetTests
{
    [Fact]
    public void Accuracy_under_token_and_time_limits()
    {
        ItemRecord It(string q, ItemStatus st, int tok, double sec) => new()
        {
            QuestionId = q, VariantId = "free", VariantType = "free", Expected = "x",
            Status = st, CompletionTokens = tok, ElapsedSeconds = sec,
        };
        var items = new List<ItemRecord>
        {
            It("a", ItemStatus.Correct, 500, 5),       // fits every limit
            It("b", ItemStatus.Correct, 1500, 25),     // needs more than 1000 tokens
            It("c", ItemStatus.Correct, 3500, 70),     // needs more than 2000 tokens and 60 s
            It("d", ItemStatus.Incorrect, 300, 3),     // wrong anyway
            It("e", ItemStatus.Truncated, 7000, 140),  // never counts
            It("f", ItemStatus.Error, 0, 0),           // not scored, not in the denominator
        };
        var b = SummaryBuilder.Build(items, items.Count).Budget!;
        Assert.Equal([1 / 5.0, 2 / 5.0, 3 / 5.0], b.AccuracyAtTokens);
        Assert.Equal([2 / 5.0, 2 / 5.0], b.AccuracyWithinSeconds);
        Assert.Equal(1500, b.MedianTokens);
        Assert.Equal(25, b.MedianSeconds);
        Assert.Equal((500 + 1500 + 3500 + 300 + 7000) / 3.0, b.TokensPerCorrect!.Value, 9);
        Assert.Equal((3 - 1) / 5.0, b.NetScore, 9);                       // 3 correct, 1 wrong free answer
        Assert.Equal(2 / ((5 + 25 + 70 + 3 + 140) / 60.0), b.NetPerMinute!.Value, 9);
    }

    [Fact]
    public void Wrong_choice_answer_costs_one_over_options_minus_one()
    {
        var opts = Enumerable.Range(0, 4).Select(k => new DisplayedOption(((char)('A' + k)).ToString(), "o" + k, "t" + k)).ToList();
        ItemRecord It(ItemStatus st) => new()
        {
            QuestionId = "q", VariantId = "mcq", VariantType = "choice", Expected = "A", Status = st,
            DisplayedOptions = opts, ElapsedSeconds = 1,
        };
        // pure guessing over 4 options: 1 correct, 3 wrong -> 1 - 3 * 1/3 = 0
        var b = SummaryBuilder.Build([It(ItemStatus.Correct), It(ItemStatus.Incorrect), It(ItemStatus.Incorrect), It(ItemStatus.Incorrect)], 4).Budget!;
        Assert.Equal(0, b.NetScore, 9);
    }

    private static LeaderboardEntry Entry(string name, int correct, int tokens)
    {
        var items = Enumerable.Range(0, 10).Select(i => new ItemRecord
        {
            QuestionId = "q" + i, VariantId = "free", VariantType = "free", Expected = "x",
            Status = i < correct ? ItemStatus.Correct : ItemStatus.Incorrect, CompletionTokens = tokens,
        }).ToList();
        return new LeaderboardEntry
        {
            RunName = name, Definition = new RunDefinition(), Header = new RunHeader(), State = RunState.Completed,
            QuestionSetHash = "x", Items = items, Summary = SummaryBuilder.Build(items, items.Count),
        };
    }

    [Fact]
    public void Pareto_marks_runs_no_other_run_beats_in_accuracy_and_length()
    {
        var best = Entry("best", 9, 900);       // most accurate
        var fast = Entry("fast", 8, 300);       // shortest
        var beaten = Entry("beaten", 7, 500);   // fast is more accurate and shorter
        var tie = Entry("tie", 8, 300);         // equal to fast: neither beats the other
        Leaderboard.MarkPareto([best, fast, beaten, tie]);
        Assert.True(best.Pareto);
        Assert.True(fast.Pareto);
        Assert.False(beaten.Pareto);
        Assert.True(tie.Pareto);
    }
}
