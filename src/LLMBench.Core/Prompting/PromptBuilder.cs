using System.Text;
using LLMBench.Core.Catalog;

namespace LLMBench.Core.Prompting;

/// <summary>An option as shown to the model: visible letter plus stable id.</summary>
public sealed record DisplayedOption(string Letter, string Id, string Text);

public static class AnswerInstructions
{
    public const string Reasoned = "reasoned";
    public const string Direct = "direct";
    public const string None = "none";
    public static readonly string[] All = [None, Reasoned, Direct];

    public static string Text(string name, bool isChoice) => (name.ToLowerInvariant(), isChoice) switch
    {
        (Reasoned, true) => "You may reason first. End your reply with one line in exactly this format:\nFinal Answer: <letter>",
        (Reasoned, false) => "You may reason first. End your reply with one line in exactly this format:\nFinal Answer: <answer>\nKeep the answer as short as possible: a number or a few words.",
        (Direct, true) => "Reply with exactly one line and nothing else:\nFinal Answer: <letter>",
        (Direct, false) => "Reply with exactly one line and nothing else:\nFinal Answer: <answer>",
        (None, _) => "",
        _ => throw new BenchException($"Unknown answer instruction '{name}' (use reasoned, direct or none)."),
    };
}

public static class PromptBuilder
{
    public static string Letter(int index) => ((char)('A' + index)).ToString();

    /// <summary>Arranges the options, shuffled reproducibly from (shuffleSeed, question id, repetition) unless fixedOrder.</summary>
    public static List<DisplayedOption> ArrangeOptions(string questionId, QuestionVariant v, bool shuffle, int shuffleSeed, int repetition)
    {
        var opts = v.Options.ToList();
        if (shuffle && !v.FixedOrder && opts.Count > 1)
        {
            var rng = new Random(StableSeed($"{shuffleSeed}|{questionId}|{repetition}"));
            for (int i = opts.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (opts[i], opts[j]) = (opts[j], opts[i]);
            }
        }
        return opts.Select((o, i) => new DisplayedOption(Letter(i), o.Id, o.Text)).ToList();
    }

    public static string BuildUserMessage(QuestionVariant v, IReadOnlyList<DisplayedOption> displayed, string answerInstruction)
    {
        var sb = new StringBuilder(v.Question.TrimEnd());
        if (v.IsChoice && displayed.Count > 0)
        {
            sb.Append("\n\n");
            sb.Append(string.Join("\n", displayed.Select(d => $"{d.Letter}. {d.Text}")));
            // No line after the options: any instruction there ("Answer with the letter.", "Give the letter of your
            // choice as the final answer.") made weak models answer at once without reasoning. The scorer accepts
            // the letter as well as the full option text.
        }
        var instr = AnswerInstructions.Text(answerInstruction, v.IsChoice);
        if (instr.Length > 0) sb.Append("\n\n").Append(instr);
        return sb.ToString();
    }

    /// <summary>
    /// Returns (system message or null, user message) after applying the placement:
    /// "system" = separate system role, "user" = system text prepended to the user message.
    /// </summary>
    public static (string? System, string User) ApplyPlacement(string? systemPrompt, string placement, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt)) return (null, userMessage);
        return string.Equals(placement, "user", StringComparison.OrdinalIgnoreCase)
            ? (null, systemPrompt.Trim() + "\n\n" + userMessage)
            : (systemPrompt.Trim(), userMessage);
    }

    /// <summary>FNV-1a hash, stable across processes (string.GetHashCode is randomized).</summary>
    public static int StableSeed(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
            return (int)(h & 0x7fffffff);
        }
    }
}
