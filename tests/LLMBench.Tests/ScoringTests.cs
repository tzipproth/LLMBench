using LLMBench.Core.Catalog;
using LLMBench.Core.Prompting;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Tests;

public class ExtractionTests
{
    [Theory]
    [InlineData("Reasoning...\nFinal Answer: B", "B")]
    [InlineData("**Final Answer: B**", "B")]
    [InlineData("**Final Answer:** B", "B")]
    [InlineData("Final answer – (B)", "(B)")]
    [InlineData("## Final Answer: $\\boxed{C}$", "C")]
    [InlineData("final answer: 0.10.", "0.10")]
    [InlineData("> Final Answer: `D`", "D")]
    [InlineData("Final Answer: A\nmore text\nFinal Answer: C", "C")]
    [InlineData("Final Answer:\n\nE", "E")]
    [InlineData("Final Answer: \"his father\"", "his father")]
    [InlineData("Final Answer: 3 cats are needed to catch 100 mice in 100 minutes. Final Answer: 3", "3")]
    [InlineData("Some steps.\nSo the car moves to square 12.\n\n\\boxed{12}", "12")]
    [InlineData("Some steps.\nThe one who did is Ben.\n$\\boxed{Ben}$", "Ben")]
    public void Extracts_last_final_answer(string content, string expected)
    {
        var ex = AnswerExtractor.Extract(content);
        Assert.Equal(expected, ex.Answer);
        Assert.Null(ex.Reason);
    }

    [Theory]
    [InlineData("Let me think.\nThe answer is B.", "no-final-line")]
    [InlineData("The lamp that stands on the desk in the corner of the room", "no-final-line")]
    [InlineData("<think>Final Answer: A</think>\nI thought about it.\nIt is A.", "no-final-line")]
    [InlineData("", "empty")]
    [InlineData("   \n ", "empty")]
    [InlineData(null, "empty")]
    [InlineData("Final Answer:", "empty")]
    [InlineData("Some steps.\nThe result is \\boxed{12}, I think.", "no-final-line")]
    public void Invalid_without_final_line(string? content, string reason)
    {
        var ex = AnswerExtractor.Extract(content);
        Assert.Null(ex.Answer);
        Assert.Equal(reason, ex.Reason);
    }

    [Theory]
    [InlineData("The ball costs **$0.05**.  \n\n**Answer: C. $0.05**", "C. 0.05")]
    [InlineData("Reasoning ...\nAnswer: 7", "7")]
    [InlineData("Answer: 3\nlater thought\nFinal Answer: 5", "5")]          // a Final Answer line always wins
    [InlineData("Answer: A\nmore\n**Answer:** B", "B")]                      // the last Answer line
    [InlineData("The farmer still has 8 sheep. Final Answer: 8", "8")]       // Final Answer in the middle of a line
    [InlineData("I'm repeating.Final Answer: D. 5", "D. 5")]
    [InlineData("So Final Answer: 3\nAnswer: 4", "3")]                       // Final Answer anywhere beats an Answer line
    [InlineData("Final Answer: 2\nso the final answer: 5", "2")]             // a Final Answer line beats one in a sentence
    [InlineData("The customer sees \"TAWI\". (Final Answer: TAWI)", "TAWI")] // in parentheses
    [InlineData("Bed (Final Answer:)", "Bed")]                               // marker after the answer
    [InlineData("B. the weekend was bad (Final Answer:)", "B. the weekend was bad")]
    [InlineData("So the Final Answer:\nB", "B")]                              // answer on the next line
    public void Answer_line_is_the_fallback_without_final_answer(string content, string expected)
    {
        var ex = AnswerExtractor.Extract(content);
        Assert.Equal(expected, ex.Answer);
        Assert.Null(ex.Reason);
    }

    [Fact]
    public void Strips_think_block()
    {
        var ex = AnswerExtractor.Extract("<think>Final Answer: A</think>\nFinal Answer: B");
        Assert.Equal("B", ex.Answer);
        Assert.True(ex.ThinkStripped);
    }

    [Theory]
    [InlineData("desk lamp", "desk lamp")]
    [InlineData("We need to simulate... Final Answer: 6\n</think>\n6", "6")]
    [InlineData("<think>long thinking</think>\n\n  **counterclockwise**  \n", "counterclockwise")]
    public void Single_short_visible_line_is_the_answer(string content, string expected)
    {
        var ex = AnswerExtractor.Extract(content);
        Assert.Equal(expected, ex.Answer);
        Assert.Null(ex.Reason);
    }

    [Fact]
    public void Unclosed_think_block_is_empty()
    {
        var ex = AnswerExtractor.Extract("<think>still thinking ... Final Answer: A");
        Assert.Null(ex.Answer);
        Assert.True(ex.ThinkStripped);
    }
}

public class UserMessageTests
{
    private static readonly QuestionVariant Choice = new()
    {
        Id = "mcq", Type = "choice", Question = "Where is the bar?",
        Options = [new() { Id = "b", Text = "blue box" }, new() { Id = "r", Text = "red box" }], CorrectOptionId = "b",
    };

    [Fact]
    public void Choice_question_with_instruction_none_is_only_question_and_options()
    {
        var d = PromptBuilder.ArrangeOptions("Q", Choice, false, 1, 0);
        Assert.Equal("Where is the bar?\n\nA. blue box\nB. red box",
                     PromptBuilder.BuildUserMessage(Choice, d, AnswerInstructions.None));
    }

    [Fact]
    public void Older_instruction_follows_the_options()
    {
        var d = PromptBuilder.ArrangeOptions("Q", Choice, false, 1, 0);
        Assert.Equal("Where is the bar?\n\nA. blue box\nB. red box\n\n" + AnswerInstructions.Text(AnswerInstructions.Reasoned, true),
                     PromptBuilder.BuildUserMessage(Choice, d, AnswerInstructions.Reasoned));
    }
}

public class ChoiceScoringTests
{
    private static readonly QuestionVariant V = new()
    {
        Id = "mcq", Type = "choice", Question = "q",
        Options = [new() { Id = "c5", Text = "$0.05" }, new() { Id = "c10", Text = "$0.10" }, new() { Id = "c15", Text = "$0.15" }, new() { Id = "c1", Text = "$1.00" }],
        CorrectOptionId = "c10",
    };

    private static readonly List<DisplayedOption> D = PromptBuilder.ArrangeOptions("Q", V, false, 1, 0);

    [Fact]
    public void Percent_with_or_without_space_matches_option()
    {
        var v = new QuestionVariant
        {
            Id = "mcq", Type = "choice",
            Options = [new() { Id = "p0", Text = "0 %" }, new() { Id = "p1", Text = "1 %" }, new() { Id = "p50", Text = "about 50 %" }],
            CorrectOptionId = "p0",
        };
        var shown = PromptBuilder.ArrangeOptions("Q", v, false, 1, 0);
        Assert.Equal(ItemStatus.Correct, Scorer.Score(v, shown, "Work.\nFinal Answer: $\\boxed{0\\%}$", "stop").Status);
        Assert.Equal(ItemStatus.Incorrect, Scorer.Score(v, shown, "Work.\nFinal Answer: 1%", "stop").Status);
    }

    [Theory]
    [InlineData("B", ItemStatus.Correct)]
    [InlineData("B.", ItemStatus.Correct)]
    [InlineData("(B)", ItemStatus.Correct)]
    [InlineData("B) 10 cents", ItemStatus.Correct)]
    [InlineData("b", ItemStatus.Correct)]
    [InlineData("Option B", ItemStatus.Correct)]
    [InlineData("A", ItemStatus.Incorrect)]
    [InlineData("$0.10", ItemStatus.Correct)]
    [InlineData("0.05", ItemStatus.Incorrect)]
    public void Scores_letters_and_option_text(string answer, ItemStatus expected)
    {
        Assert.Equal(expected, Scorer.ScoreChoice(V, D, answer).Status);
    }

    [Theory]
    [InlineData("E", "out-of-range")]
    [InlineData("B or C", "ambiguous")]
    [InlineData("B, C", "ambiguous")]
    [InlineData("ten cents", "unparseable")]
    [InlineData("The ball costs ten cents", "unparseable")]
    [InlineData("The ball costs $0.05, not $0.10", "ambiguous")]
    public void Invalid_choice_answers(string answer, string reason)
    {
        var r = Scorer.ScoreChoice(V, D, answer);
        Assert.Equal(ItemStatus.Invalid, r.Status);
        Assert.Equal(reason, r.Reason);
    }

    [Theory]
    [InlineData("The ball costs $0.10", ItemStatus.Correct)]
    [InlineData("The ball costs $0.05.", ItemStatus.Incorrect)]
    [InlineData("The ball costs \\$0.10", ItemStatus.Correct)]
    [InlineData("\\(\\boxed{\\$0.10}\\)", ItemStatus.Correct)]
    public void Sentence_naming_one_option_counts(string answer, ItemStatus expected)
    {
        Assert.Equal(expected, Scorer.ScoreChoice(V, D, answer).Status);
    }

    [Theory]
    [InlineData("9 minutes, because each egg takes 3 minutes", null)]          // two options named -> ambiguous
    [InlineData("A total of 9 minutes", "9")]                                  // "A" is a word here, not letter A
    [InlineData("Lea looks in the blue box first", "blue")]                   // whole option text inside the sentence
    [InlineData("It takes 0.5 hours", null)]                                    // "5" inside "0.5" is not option "5"
    public void Sentence_matching_details(string answer, string? expectedId)
    {
        var v = new QuestionVariant
        {
            Type = "choice", Question = "q", CorrectOptionId = "9",
            Options = [new() { Id = "3", Text = "3 minutes" }, new() { Id = "9", Text = "9 minutes" },
                       new() { Id = "blue", Text = "in the blue box" }, new() { Id = "5", Text = "5" }],
        };
        var d = PromptBuilder.ArrangeOptions("Q", v, false, 1, 0);
        Assert.Equal(expectedId, Scorer.ScoreChoice(v, d, answer).ChosenOptionId);
    }

    [Fact]
    public void Option_text_starting_with_capital_A_is_not_letter_A()
    {
        var v = new QuestionVariant
        {
            Type = "choice", Question = "q", CorrectOptionId = "x",
            Options = [new() { Id = "y", Text = "nothing" }, new() { Id = "x", Text = "A juggler" }],
        };
        var d = PromptBuilder.ArrangeOptions("Q", v, false, 1, 0);
        var r = Scorer.ScoreChoice(v, d, "A juggler");
        Assert.Equal(ItemStatus.Correct, r.Status);
        Assert.Equal("B", r.ChosenLetter);
    }

    [Fact]
    public void Truncated_overrides_answer()
    {
        var r = Scorer.Score(V, D, "Final Answer: B", "length");
        Assert.Equal(ItemStatus.Truncated, r.Status);
    }

    [Fact]
    public void Full_reply_scoring()
    {
        Assert.Equal(ItemStatus.Correct, Scorer.Score(V, D, "The ball costs 10 cents.\n\n**Final Answer: B**", "stop").Status);
        Assert.Equal(ItemStatus.Invalid, Scorer.Score(V, D, "The ball costs 10 cents, so B.", "stop").Status);
    }

    [Fact]
    public void Shuffle_is_reproducible_and_keeps_solution()
    {
        var a = PromptBuilder.ArrangeOptions("CS-003", V, true, 7, 0);
        var b = PromptBuilder.ArrangeOptions("CS-003", V, true, 7, 0);
        Assert.Equal(a, b);
        var correct = a.Single(o => o.Id == "c10");
        Assert.Equal(ItemStatus.Correct, Scorer.ScoreChoice(V, a, correct.Letter).Status);
    }

    [Fact]
    public void Fixed_order_is_never_shuffled()
    {
        var v = new QuestionVariant { Type = "choice", FixedOrder = true, Options = V.Options, CorrectOptionId = "c10" };
        var d = PromptBuilder.ArrangeOptions("CS-003", v, true, 7, 3);
        Assert.Equal(["c5", "c10", "c15", "c1"], d.Select(o => o.Id));
    }
}

public class FreeScoringTests
{
    private static QuestionVariant Num(params string[] accepted) =>
        new() { Type = "free", AnswerType = "number", Question = "q", AcceptedAnswers = accepted.ToList() };

    private static QuestionVariant Text(params string[] accepted) =>
        new() { Type = "free", AnswerType = "text", Question = "q", AcceptedAnswers = accepted.ToList() };

    [Theory]
    [InlineData("0.10", ItemStatus.Correct)]
    [InlineData("$0.10", ItemStatus.Correct)]
    [InlineData("0.1 dollars", ItemStatus.Correct)]
    [InlineData("0,10", ItemStatus.Correct)]
    [InlineData("10 cents", ItemStatus.Incorrect)]
    [InlineData("0.05", ItemStatus.Incorrect)]
    public void Numbers(string answer, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Num("0.10"), answer).Status);

    [Theory]
    [InlineData("nine", ItemStatus.Correct)]
    [InlineData("9 cubes", ItemStatus.Correct)]
    [InlineData("1", ItemStatus.Incorrect)]
    public void Number_words(string answer, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Num("9"), answer).Status);

    [Theory]
    [InlineData("8 or 16", "ambiguous")]
    [InlineData("many", "no-number")]
    public void Invalid_numbers(string answer, string reason)
    {
        var r = Scorer.ScoreFree(Num("8"), answer);
        Assert.Equal(ItemStatus.Invalid, r.Status);
        Assert.Equal(reason, r.Reason);
    }

    [Theory]
    [InlineData("5 minutes (since each machine takes 1 minute to make 1 widget)", "5", ItemStatus.Correct)]
    [InlineData("50 minutes (10 machines make 10 widgets in 1 minute (roughly))", "50", ItemStatus.Correct)]
    [InlineData("46 (3 rows * 4 eggs per row - 2 broken eggs)", "10", ItemStatus.Incorrect)]
    public void Explanations_in_parentheses_are_ignored(string answer, string accepted, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Num(accepted), answer).Status);

    [Theory]
    [InlineData("6 (excluding end posts) + 2 (end posts) = 8 posts in total")]
    [InlineData("All 12 months have at least 28 days")]
    [InlineData("3 or 4")]
    [InlineData("6-8 posts")]
    [InlineData("6 to 8")]
    [InlineData("The answer is 6, maybe 8")]
    public void Several_numbers_outside_parentheses_stay_ambiguous(string answer)
    {
        var r = Scorer.ScoreFree(Num("6"), answer);
        Assert.Equal(ItemStatus.Invalid, r.Status);
        Assert.Equal("ambiguous", r.Reason);
    }

    [Theory]
    [InlineData("€463.05 ≈ €463 after three years of 5 % compound interest.", "463.05", ItemStatus.Correct)]
    [InlineData("€525.50 missing, total costs €938 vs. €412.50 cash on hand.", "525.50", ItemStatus.Correct)]
    [InlineData("938 euros, of which 525.50 are missing", "525.50", ItemStatus.Incorrect)]
    public void Leading_number_is_the_answer(string answer, string accepted, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Num(accepted), answer).Status);

    private const string Widgets = "If 5 machines take 5 minutes to make 5 widgets, how long would 10 machines take to make 100 widgets?";

    [Theory]
    [InlineData("10 machines need 50 minutes to make 100 widgets", "50", ItemStatus.Correct)]
    [InlineData("It takes 10 machines 25 minutes for 100 widgets", "50", ItemStatus.Incorrect)]
    public void One_number_not_in_the_question_is_the_answer(string answer, string accepted, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(new QuestionVariant { Type = "free", AnswerType = "number", Question = Widgets, AcceptedAnswers = [accepted] }, answer).Status);

    [Theory]
    [InlineData("10 machines need 5 minutes to make 100 widgets")]          // no new number
    [InlineData("10 machines need 50 or 60 minutes for 100 widgets")]     // two new numbers
    [InlineData("50 minutes, with 10 machines = 1 widget every 30 s")]     // formula: a new number can be a side value
    public void Sentence_without_exactly_one_new_number_stays_ambiguous(string answer)
    {
        var r = Scorer.ScoreFree(new QuestionVariant { Type = "free", AnswerType = "number", Question = Widgets, AcceptedAnswers = ["50"] }, answer);
        Assert.Equal(ItemStatus.Invalid, r.Status);
        Assert.Equal("ambiguous", r.Reason);
    }

    [Theory]
    [InlineData("8th day", "8", ItemStatus.Correct)]
    [InlineData("on the 2nd floor", "2", ItemStatus.Correct)]
    [InlineData("the 3rd floor", "2", ItemStatus.Incorrect)]
    [InlineData("8 things", "8", ItemStatus.Correct)]
    public void Ordinal_digits_count_as_numbers(string answer, string accepted, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Num(accepted), answer).Status);

    [Fact]
    public void Same_number_twice_is_not_ambiguous() =>
        Assert.Equal(ItemStatus.Correct, Scorer.ScoreFree(Num("4"), "4 (four)").Status);

    [Fact]
    public void Thousands_separator() =>
        Assert.Equal(ItemStatus.Correct, Scorer.ScoreFree(Num("1000"), "1,000").Status);

    [Theory]
    [InlineData("Venom", ItemStatus.Correct)]
    [InlineData("the venom.", ItemStatus.Correct)]
    [InlineData("  poison ", ItemStatus.Correct)]
    [InlineData("gift", ItemStatus.Incorrect)]
    public void Text_answers(string answer, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Text("venom", "poison"), answer).Status);

    [Fact]
    public void Clock_text() =>
        Assert.Equal(ItemStatus.Correct, Scorer.ScoreFree(Text("9:00", "09:00"), "9:00").Status);

    [Theory]
    [InlineData("16:10 (16 hours and 10 minutes)", ItemStatus.Correct)]
    [InlineData("16:10 (not 15:10)", ItemStatus.Correct)]
    [InlineData("15:10 (or 16:10)", ItemStatus.Incorrect)]
    public void Text_answers_ignore_remarks_in_parentheses(string answer, ItemStatus expected) =>
        Assert.Equal(expected, Scorer.ScoreFree(Text("16:10"), answer).Status);
}

public class StatsTests
{
    [Fact]
    public void Wilson_interval_matches_reference()
    {
        var (lo, hi) = SummaryBuilder.Wilson(37, 59);
        Assert.InRange(lo, 0.497, 0.503);
        Assert.InRange(hi, 0.737, 0.741); // 49.9–73.9 %, printed as 50–74 % in the concept
    }

    [Fact]
    public void Wilson_extremes()
    {
        var (lo, hi) = SummaryBuilder.Wilson(10, 10);
        Assert.Equal(1.0, hi, 6);
        Assert.InRange(lo, 0.69, 0.73);
    }
}
