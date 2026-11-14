using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMBench.Core.Scoring;

/// <summary>Pure text helpers used for extraction and comparison.</summary>
public static partial class AnswerNormalizer
{
    [GeneratedRegex(@"\\boxed\s*\{([^{}]*)\}")]
    private static partial Regex Boxed();

    [GeneratedRegex(@"\\text\s*\{([^{}]*)\}")]
    private static partial Regex LatexText();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(a|an|the)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticle();

    // 1,000,000.5 | 1000.5 | 0,5 | .5 | -3 | 8th, 2nd (an ordinal suffix is not part of the token)
    [GeneratedRegex(@"(?<![\w.,])[-−]?(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:[.,]\d+)?|\.\d+)(?=(?i:st|nd|rd|th)?(?!\w))")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\b(zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWord();

    private static readonly string[] Words =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    /// <summary>Removes markdown, quotes, $, \boxed{...} and a trailing period from an extracted answer.</summary>
    public static string CleanAnswer(string raw)
    {
        // LaTeX escapes: "\(\boxed{\$0.05}\)" -> "0.05"
        var s = raw.Trim().Replace("\\$", "$").Replace("\\%", "%");
        for (int i = 0; i < 3; i++)
        {
            s = Boxed().Replace(s, "$1");
            s = LatexText().Replace(s, "$1");
        }
        s = s.Replace("\\(", "").Replace("\\)", "").Replace("\\[", "").Replace("\\]", "");
        foreach (var ch in new[] { "*", "`", "$", "\"", "“", "”", "„", "«", "»" }) s = s.Replace(ch, "");
        s = s.Trim().Trim('_', '\'', '‘', '’', '#', '>').Trim();
        while (s.EndsWith('.') && !s.EndsWith("..", StringComparison.Ordinal)) s = s[..^1].TrimEnd();
        return s.Trim();
    }

    /// <summary>Normalization for free text comparison: lower case, collapsed whitespace, no trailing period, no leading article.</summary>
    public static string NormalizeText(string s)
    {
        var t = CleanAnswer(s).ToLowerInvariant().Replace('’', '\'');
        t = Spaces().Replace(t, " ").Trim().TrimEnd('.', '!', ';').Trim();
        t = LeadingArticle().Replace(t, "");
        return t;
    }

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex Parenthesized();

    /// <summary>Removes "(...)" remarks (also nested ones) from a text.</summary>
    public static string RemoveParentheses(string text)
    {
        var t = text;
        for (int i = 0; i < 5; i++)
        {
            var next = Parenthesized().Replace(t, " ");
            if (next == t) break;
            t = next;
        }
        return t;
    }

    /// <summary>All numbers found in a text (digits and the words zero..twelve).</summary>
    public static List<double> FindNumbers(string text)
    {
        var found = new List<(int Pos, double Value)>();
        foreach (Match m in NumberToken().Matches(text))
            if (TryParseToken(m.Value, out var d)) found.Add((m.Index, d));
        foreach (Match m in NumberWord().Matches(text))
            found.Add((m.Index, Array.IndexOf(Words, m.Value.ToLowerInvariant())));
        return found.OrderBy(f => f.Pos).Select(f => f.Value).ToList();
    }

    /// <summary>Exactly one distinct number in the text, else null.</summary>
    public static double? ParseSingleNumber(string text)
    {
        var nums = FindNumbers(text).Distinct().ToList();
        return nums.Count == 1 ? nums[0] : null;
    }

    private static bool TryParseToken(string token, out double value)
    {
        var t = token.Replace('−', '-');
        if (Regex.IsMatch(t, @"^-?\d{1,3}(,\d{3})+(\.\d+)?$")) t = t.Replace(",", "");
        else t = t.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool NumbersEqual(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(b));
}
