using System.Text.RegularExpressions;

namespace LLMBench.Core.Scoring;

public sealed record Extraction(string? Answer, string? Reason, bool ThinkStripped, string VisibleContent);

/// <summary>Finds the final answer line in a model reply (concept section 10.1). Never guesses from free text.</summary>
public static partial class AnswerExtractor
{
    /// <summary>A reply without a final line counts only if its visible part is one line of at most this size.</summary>
    public const int MaxSingleLineWords = 6;
    public const int MaxSingleLineChars = 60;

    [GeneratedRegex(@"(?im)^[ \t>*_#`]*final[ \t]+answer[ \t*_`]*[:\-–][ \t]*(.*?)[ \t]*$")]
    private static partial Regex FinalLine();

    // first fallback: "Final Answer:" in the middle of a line ("... still has 8 sheep. Final Answer: 8", Mistral);
    // group 1 = text before it on that line (greedy: the last occurrence in the line), group 2 = text after it
    [GeneratedRegex(@"(?im)^(.*)final[ \t]+answer[ \t*_`]*[:\-–][ \t]*(.*?)[ \t]*$")]
    private static partial Regex FinalInLine();

    // second fallback when no "Final Answer:" exists at all: a line that starts with "Answer:" ("**Answer: C. $0.05**", gpt-oss)
    [GeneratedRegex(@"(?im)^[ \t>*_#`]*answer[ \t*_`]*[:\-–][ \t]*(.*?)[ \t]*$")]
    private static partial Regex AnswerLine();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    public static Extraction Extract(string? content)
    {
        var text = content ?? "";
        bool stripped = false;

        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase) || text.Contains("</think>", StringComparison.OrdinalIgnoreCase))
        {
            stripped = true;
            text = ThinkBlock().Replace(text, "");
            // unclosed <think> at the start: everything after it is thinking
            var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open >= 0) text = text[..open];
            // closing tag without opening tag (template already opened the block)
            var close = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (close >= 0) text = text[(close + "</think>".Length)..];
        }

        if (string.IsNullOrWhiteSpace(text)) return new Extraction(null, "empty", stripped, text);

        var matches = FinalLine().Matches(text);
        if (matches.Count == 0)
        {
            var inLine = FinalInLine().Matches(text);
            if (inLine.Count > 0)
            {
                var m = inLine[^1];
                var before = m.Groups[1].Value.TrimEnd();
                var after = m.Groups[2].Value.Trim();
                bool inParens = before.EndsWith('(');
                if (inParens)
                {
                    // "(Final Answer: TAWI)" -> TAWI;  "Bed (Final Answer:)" -> the text before the marker is the answer
                    before = before[..^1].TrimEnd();
                    if (after.EndsWith(')')) after = after[..^1].TrimEnd();
                    if (after.Length == 0) after = before;
                }
                if (after.Length == 0)
                    after = text[(m.Index + m.Length)..].Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                var inLineAnswer = AnswerNormalizer.CleanAnswer(after);
                return inLineAnswer.Length == 0
                    ? new Extraction(null, "empty", stripped, text)
                    : new Extraction(inLineAnswer, null, stripped, text);
            }
            matches = AnswerLine().Matches(text);
        }
        if (matches.Count == 0)
        {
            // The whole visible reply (after any thinking) is one short line, e.g. "</think>\n6" or just "desk lamp":
            // that line is the answer the user sees. Longer text without a final line stays invalid (no guessing).
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 1 && lines[0].Length <= MaxSingleLineChars &&
                lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxSingleLineWords)
            {
                var single = AnswerNormalizer.CleanAnswer(lines[0]);
                if (single.Length > 0) return new Extraction(single, null, stripped, text);
            }
            return new Extraction(null, "no-final-line", stripped, text);
        }

        var last = matches[^1];
        var raw = last.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(raw) || AnswerNormalizer.CleanAnswer(raw).Length == 0)
        {
            // "Final Answer:" followed by the answer on the next non-empty line
            var rest = text[(last.Index + last.Length)..].Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            raw = rest ?? "";
        }

        var cleaned = AnswerNormalizer.CleanAnswer(raw);
        return cleaned.Length == 0
            ? new Extraction(null, "empty", stripped, text)
            : new Extraction(cleaned, null, stripped, text);
    }
}
