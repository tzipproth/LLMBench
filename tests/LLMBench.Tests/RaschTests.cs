using LLMBench.Core.Prompting;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Tests;

public class RaschTests
{
    private static ItemRecord Item(int q, bool correct, int options = 0, ItemStatus? status = null) => new()
    {
        QuestionId = $"Q{q:000}", VariantId = options > 0 ? "mcq" : "free", VariantType = options > 0 ? "choice" : "free",
        DisplayedOptions = Enumerable.Range(0, options).Select(k => new DisplayedOption(((char)('A' + k)).ToString(), $"o{k}", $"t{k}")).ToList(),
        Status = status ?? (correct ? ItemStatus.Correct : ItemStatus.Incorrect),
    };

    // run "skill" s solves question q exactly when q < s * questions (a clean difficulty ladder)
    private static List<ItemRecord> Ladder(double skill, int questions = 40) =>
        Enumerable.Range(0, questions).Select(q => Item(q, q < skill * questions)).ToList();

    private static Calibration Reference() => Rasch.Calibrate(
        new[] { 0.2, 0.4, 0.6, 0.8, 0.9 }.Select(s => ($"run{(int)Math.Round(s * 100)}", "m", (IReadOnlyList<ItemRecord>)Ladder(s))).ToList(), "hash", "cat.json");

    [Fact]
    public void Stronger_runs_get_higher_scores_and_harder_items_higher_difficulty()
    {
        var cal = Reference();
        var scores = cal.Runs.Select(r => r.Score).ToList();
        Assert.Equal(scores.OrderBy(x => x), scores);
        Assert.True(cal.Items.Single(i => i.Key == "Q035/free").Difficulty > cal.Items.Single(i => i.Key == "Q005/free").Difficulty);
        Assert.Equal(0, cal.Items.Average(i => i.Difficulty), 3);       // the scale is anchored at mean difficulty 0
    }

    [Fact]
    public void Estimate_with_frozen_difficulties_reproduces_the_reference_and_places_new_runs()
    {
        var cal = Reference();
        var again = Rasch.Estimate(cal, Ladder(0.6))!;
        Assert.Equal(cal.Runs.Single(r => r.Run == "run60").Score, again.Score, 0);
        var newcomer = Rasch.Estimate(cal, Ladder(0.7))!;          // a run added later lands between its neighbours
        Assert.InRange(newcomer.Score, cal.Runs.Single(r => r.Run == "run60").Score, cal.Runs.Single(r => r.Run == "run80").Score);
        Assert.True(newcomer.Score95 > 0);
    }

    [Fact]
    public void Correct_answers_on_choice_questions_count_less_than_on_free_questions()
    {
        // the same item difficulties, once with a guessing floor of 1/4
        var free = new Calibration { Items = Enumerable.Range(0, 20).Select(q => new CalibrationItem { Key = $"Q{q:000}/free", Difficulty = 0 }).ToList() };
        var choice = new Calibration { Items = Enumerable.Range(0, 20).Select(q => new CalibrationItem { Key = $"Q{q:000}/mcq", Difficulty = 0, Guess = 0.25 }).ToList() };
        var a = Rasch.Estimate(free, Enumerable.Range(0, 20).Select(q => Item(q, q < 10)).ToList())!;
        var b = Rasch.Estimate(choice, Enumerable.Range(0, 20).Select(q => Item(q, q < 10, options: 4)).ToList())!;
        Assert.True(b.Score < a.Score);
    }

    [Fact]
    public void Errors_are_ignored_invalid_and_truncated_count_as_wrong()
    {
        Assert.Null(Rasch.Response(Item(1, false, status: ItemStatus.Error)));
        Assert.Null(Rasch.Response(Item(1, false, status: ItemStatus.Skipped)));
        Assert.Equal(0, Rasch.Response(Item(1, false, status: ItemStatus.Invalid)));
        Assert.Equal(0, Rasch.Response(Item(1, false, status: ItemStatus.Truncated)));
        Assert.Equal(0.25, Rasch.GuessOf(Item(1, true, options: 4)));
        Assert.Equal(0, Rasch.GuessOf(Item(1, true)));
    }

    [Fact]
    public void Strength_is_the_odds_factor_against_the_reference_run()
    {
        var runs = new[] { 0.2, 0.4, 0.6, 0.8 }.Select(s => ($"run{(int)Math.Round(s * 100)}", "m", (IReadOnlyList<ItemRecord>)Ladder(s))).ToList();
        var cal = Rasch.Calibrate(runs, "hash", "cat.json", referenceRun: "run40");
        Assert.Equal("run40", cal.ReferenceRun);
        var reference = Rasch.Estimate(cal, Ladder(0.4))!;
        var strong = Rasch.Estimate(cal, Ladder(0.8))!;
        var weak = Rasch.Estimate(cal, Ladder(0.2))!;
        Assert.Equal(1, reference.Strength!.Value, 3);
        Assert.True(strong.Strength > 1 && weak.Strength < 1);
        // ratio scale: the factor between two runs follows from the score difference (400 points = 10 times)
        Assert.Equal(Math.Pow(10, (strong.Score - weak.Score) / 400), strong.Strength!.Value / weak.Strength!.Value, 6);
        Assert.InRange(strong.Strength!.Value, strong.StrengthLow!.Value, strong.StrengthHigh!.Value);
        Assert.Null(Rasch.Estimate(Reference(), Ladder(0.4))!.Strength);              // no reference -> no strength
        Assert.Throws<LLMBench.Core.BenchException>(() => Rasch.Calibrate(runs, "hash", "cat.json", referenceRun: "nope"));
    }

    [Theory]
    [InlineData(83.7, "84")]
    [InlineData(7.84, "7.8")]
    [InlineData(0.384, "0.38")]
    [InlineData(0.99997, "1.0")]
    [InlineData(9.97, "10")]
    public void Strength_formatting(double value, string expected) => Assert.Equal(expected, Rasch.FormatStrength(value));

    [Fact]
    public void A_run_that_solves_everything_gets_a_finite_score() =>
        Assert.True(double.IsFinite(Rasch.Estimate(Reference(), Ladder(1.0))!.Score));
}
