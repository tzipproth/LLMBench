using System.Text.RegularExpressions;
using LLMBench.Core.Catalog;
using LLMBench.Core.Prompting;

namespace LLMBench.Core.Scoring;

public enum ItemStatus { Correct, Incorrect, Invalid, Truncated, Error, Skipped }

public sealed record ScoreResult(
    ItemStatus Status,
    string? Reason,
    string? ExtractedAnswer,
    string? ChosenLetter,
    string? ChosenOptionId,
    bool ThinkStripped);

/// <summary>Automatic scoring against the stored solution (concept section 10.2). Pure functions.</summary>
public static partial class Scorer
{
    // leading letter: B | B. | B) | (B) | [B] | B: | B - text
    [GeneratedRegex(@"^[\(\[]?\s*([A-Za-z])\s*[\)\]]?(?=$|[\s.:)\]\-–,;])")]
    private static partial Regex LeadingLetter();

    [GeneratedRegex(@"^(?:option|answer|choice)\s*[:\-]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingWord();

    [GeneratedRegex(@"^\s*(?:,|/|&|and|or)\s*[\(\[]?[A-Z](?:[\)\].\s,]|$)")]
    private static partial Regex SecondLetter();

    // the answer starts with a number, optionally after a currency sign: "€525.50 missing", "463.05 ≈ 463"
    [GeneratedRegex(@"^\s*[€$£]?\s*[-−]?(?:\d[\d,]*(?:\.\d+)?|\.\d+)")]
    private static partial Regex LeadingNumber();

    // after the leading number: an alternative, a range or a calculation makes the line ambiguous
    [GeneratedRegex(@"[=+×*÷/]|\bor\b|\bto\b|^\s*[a-zA-Z%€$£]*\s*[-–]\s*[€$£]?\d", RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeOrCalculation();

    public static ScoreResult Score(QuestionVariant variant, IReadOnlyList<DisplayedOption> displayed, string? content, string? finishReason)
    {
        var ex = AnswerExtractor.Extract(content);
        bool truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);

        if (ex.Answer is null)
            return new ScoreResult(truncated ? ItemStatus.Truncated : ItemStatus.Invalid,
                truncated ? "length" : ex.Reason, null, null, null, ex.ThinkStripped);

        var r = variant.IsChoice ? ScoreChoice(variant, displayed, ex.Answer) : ScoreFree(variant, ex.Answer);
        r = r with { ThinkStripped = ex.ThinkStripped };
        if (truncated) r = r with { Status = ItemStatus.Truncated, Reason = "length" };
        return r;
    }

    public static ScoreResult ScoreChoice(QuestionVariant variant, IReadOnlyList<DisplayedOption> displayed, string answer)
    {
        var a = LeadingWord().Replace(answer.Trim(), "");
        // a bare letter: "b", "B.", "(b)", "[B]"
        bool standsAlone = Regex.IsMatch(a, @"^[\(\[]?\s*[A-Za-z]\s*[\)\]]?\s*\.?\s*$");

        // The answer is exactly the text of one option (e.g. "A juggler ..." must not be read as letter A)
        var fullText = MatchOptionText(displayed, answer);
        if (fullText != null && !standsAlone) return Decide(variant, fullText, answer);

        var m = LeadingLetter().Match(a);
        if (m.Success)
        {
            var letterRaw = m.Groups[1].Value;
            var rest = a[m.Length..];
            bool isUpper = char.IsUpper(letterRaw[0]);
            // a lower-case letter only counts when it stands alone
            // "A total of 5 crossings ..." / "I would pick 7": the English words A and I start a sentence, not a
            // letter choice, when the sentence names exactly one option
            bool sentence = m.Value is "A" or "I" && Regex.IsMatch(rest, @"^\s+\p{Ll}") &&
                            OptionsInSentence(displayed, answer).Count == 1;
            if ((isUpper || standsAlone) && !sentence)
            {
                var letter = letterRaw.ToUpperInvariant();
                if (SecondLetter().IsMatch(rest))
                    return new ScoreResult(ItemStatus.Invalid, "ambiguous", answer, null, null, false);
                var opt = displayed.FirstOrDefault(d => d.Letter == letter);
                if (opt == null)
                    return new ScoreResult(ItemStatus.Invalid, "out-of-range", answer, letter, null, false);
                return Decide(variant, opt, answer);
            }
        }

        if (fullText != null) return Decide(variant, fullText, answer);

        // The final line is a sentence that names the content of an option: "The ball costs $0.05." Only the final
        // line is searched (never the reasoning); the option text must occur as whole words. Two options -> ambiguous.
        var inSentence = OptionsInSentence(displayed, answer);
        return inSentence.Count switch
        {
            1 => Decide(variant, inSentence[0], answer),
            > 1 => new ScoreResult(ItemStatus.Invalid, "ambiguous", answer, null, null, false),
            _ => new ScoreResult(ItemStatus.Invalid, "unparseable", answer, null, null, false),
        };
    }

    private static DisplayedOption? MatchOptionText(IReadOnlyList<DisplayedOption> displayed, string answer)
    {
        var norm = AnswerNormalizer.NormalizeText(answer);
        var hits = displayed.Where(d => AnswerNormalizer.NormalizeText(d.Text) == norm).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Options whose text occurs as whole words in the answer; an option contained in a longer hit
    /// ("blue box" inside "in the blue box") is dropped.</summary>
    public static List<DisplayedOption> OptionsInSentence(IReadOnlyList<DisplayedOption> displayed, string answer)
    {
        var norm = AnswerNormalizer.NormalizeText(answer);
        var hits = displayed
            .Select(d => (Option: d, Text: AnswerNormalizer.NormalizeText(d.Text)))
            .Where(h => h.Text.Length > 0 &&
                        Regex.IsMatch(norm, @"(?<![\p{L}\p{N}])(?<!\d[.,])" + Regex.Escape(h.Text) + @"(?![\p{L}\p{N}]|[.,]\d)"))
            .ToList();
        return hits.Where(h => !hits.Any(o => o.Text.Length > h.Text.Length && o.Text.Contains(h.Text)))
                   .Select(h => h.Option).ToList();
    }

    private static ScoreResult Decide(QuestionVariant variant, DisplayedOption chosen, string answer) =>
        new(chosen.Id == variant.CorrectOptionId ? ItemStatus.Correct : ItemStatus.Incorrect,
            null, answer, chosen.Letter, chosen.Id, false);

    public static ScoreResult ScoreFree(QuestionVariant variant, string answer)
    {
        if (variant.IsNumber)
        {
            var nums = AnswerNormalizer.FindNumbers(answer).Distinct().ToList();
            if (nums.Count > 1)
            {
                // "5 minutes (since each machine takes 1 minute ...)": remarks in parentheses explain the answer,
                // they are not part of it. Only if exactly one number remains outside them is that the answer.
                var outside = AnswerNormalizer.FindNumbers(AnswerNormalizer.RemoveParentheses(answer)).Distinct().ToList();
                if (outside.Count == 1) nums = outside;
            }
            if (nums.Count > 1)
            {
                // "€525.50 missing, total costs €938 vs. €412.50": the line starts with the answer, the rest comments on it.
                // Not when the rest offers alternatives or a calculation ("3 or 4", "4-6", "6 + 2 = 8").
                var lead = LeadingNumber().Match(answer);
                if (lead.Success && !AlternativeOrCalculation().IsMatch(answer[lead.Length..]))
                    nums = [AnswerNormalizer.FindNumbers(answer)[0]];
            }
            if (nums.Count > 1)
            {
                // "10 machines need 50 minutes to make 100 widgets": a sentence that repeats numbers from the question.
                // If exactly one number in it does not occur in the question, that number is the answer.
                // Not for lines with formulas ("3 litres in A, with B=2 L and C=3 L"): there a new number can be a side value.
                var asked = AnswerNormalizer.FindNumbers(variant.Question);
                var fresh = nums.Where(n => !asked.Any(q => AnswerNormalizer.NumbersEqual(n, q))).ToList();
                if (fresh.Count == 1 && !answer.Contains('=')) nums = fresh;
            }
            if (nums.Count == 0) return new ScoreResult(ItemStatus.Invalid, "no-number", answer, null, null, false);
            if (nums.Count > 1) return new ScoreResult(ItemStatus.Invalid, "ambiguous", answer, null, null, false);
            bool ok = variant.AcceptedAnswers
                .Select(AnswerNormalizer.ParseSingleNumber)
                .Any(acc => acc.HasValue && AnswerNormalizer.NumbersEqual(nums[0], acc.Value));
            return new ScoreResult(ok ? ItemStatus.Correct : ItemStatus.Incorrect, null, answer, null, null, false);
        }

        var norm = AnswerNormalizer.NormalizeText(answer);
        bool match = variant.AcceptedAnswers.Any(acc => AnswerNormalizer.NormalizeText(acc) == norm);
        if (!match)
        {
            // like numbers: "16:10 (16 hours and 10 minutes)" - a remark in parentheses explains the answer, it is not part of it
            var outside = AnswerNormalizer.NormalizeText(AnswerNormalizer.RemoveParentheses(answer));
            if (outside.Length > 0 && outside != norm)
                match = variant.AcceptedAnswers.Any(acc => AnswerNormalizer.NormalizeText(acc) == outside);
        }
        return new ScoreResult(match ? ItemStatus.Correct : ItemStatus.Incorrect, null, answer, null, null, false);
    }
}
