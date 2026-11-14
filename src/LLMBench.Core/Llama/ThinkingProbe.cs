using LLMBench.Core.Runs;

namespace LLMBench.Core.Llama;

public sealed class ThinkingProbeResult
{
    /// <summary>True when the run's thinking setting is effective (off: no reasoning; on: reasoning).</summary>
    public bool Verified { get; set; }
    public bool OnThinks { get; set; }
    public bool OffThinks { get; set; }
    public string Note { get; set; } = "";
    public int OnReasoningChars { get; set; }
    public int OffReasoningChars { get; set; }
    public int? OnCompletionTokens { get; set; }
    public int? OffCompletionTokens { get; set; }
    public bool OnThinkInContent { get; set; }
    public bool OffThinkInContent { get; set; }
    public string? OnError { get; set; }
    public string? OffError { get; set; }
    public double ElapsedSeconds { get; set; }
}

/// <summary>
/// Concept 8.3 step 4: sends the same short question once with the ON fragment and once with the OFF fragment
/// and checks that thinking really changes (reasoning_content filled vs. empty, or clearly more tokens).
/// </summary>
public static class ThinkingProbe
{
    public const string Question =
        "How many times does the letter r occur in the word \"strawberry\"? End your reply with one line: Final Answer: <number>";

    public static async Task<ThinkingProbeResult> RunAsync(LlamaClient client, RunDefinition def, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(def.TimeoutSeconds, 300));
        var on = def.Clone();
        on.Thinking = "on";
        var off = def.Clone();
        off.Thinking = "off";

        var r = new ThinkingProbeResult();
        var resOn = await client.ChatAsync(RequestBuilder.Build(on, null, Question, def.Seed), timeout, ct).ConfigureAwait(false);
        var resOff = await client.ChatAsync(RequestBuilder.Build(off, null, Question, def.Seed), timeout, ct).ConfigureAwait(false);
        r.ElapsedSeconds = resOn.ElapsedSeconds + resOff.ElapsedSeconds;
        r.OnError = resOn.Error;
        r.OffError = resOff.Error;
        if (!resOn.Success || !resOff.Success)
        {
            r.Note = $"probe request failed: {resOn.Error ?? resOff.Error}";
            return r;
        }

        r.OnReasoningChars = resOn.ReasoningContent?.Length ?? 0;
        r.OffReasoningChars = resOff.ReasoningContent?.Length ?? 0;
        r.OnCompletionTokens = resOn.CompletionTokens;
        r.OffCompletionTokens = resOff.CompletionTokens;
        r.OnThinkInContent = HasThink(resOn.Content);
        r.OffThinkInContent = HasThink(resOff.Content);

        bool onThinks = r.OnReasoningChars > 0 || r.OnThinkInContent;
        bool offThinks = r.OffReasoningChars > 0 || r.OffThinkInContent;
        int tOn = r.OnCompletionTokens ?? 0, tOff = r.OffCompletionTokens ?? 0;
        // no separate reasoning, but clearly more tokens with ON: the model thinks inside the answer text
        if (!onThinks && !offThinks && tOn > 2 * tOff + 50) onThinks = true;
        r.OnThinks = onThinks;
        r.OffThinks = offThinks;
        var facts = $"ON: {(onThinks ? "reasoning" : "no reasoning")} ({r.OnReasoningChars} chars, {tOn} tokens), OFF: {(offThinks ? "reasoning" : "no reasoning")} ({r.OffReasoningChars} chars, {tOff} tokens)";

        switch (def.Thinking)
        {
            case "off":
                // what matters for "off": no reasoning happens (also true for models without a thinking mode)
                r.Verified = !offThinks;
                r.Note = (offThinks ? "the OFF fragment does not switch thinking off. " : !onThinks ? "the model has no (switchable) thinking mode - thinking is off. " : "thinking is off. ") + facts;
                break;
            case "on":
                r.Verified = onThinks;
                r.Note = (onThinks ? "thinking is on. " : "the ON fragment does not switch thinking on (template/server ignores it?). ") + facts;
                break;
            default:
                r.Verified = onThinks && !offThinks;
                r.Note = (onThinks && !offThinks ? "the ON/OFF switch works. "
                          : !onThinks && !offThinks ? "the model shows no thinking with either fragment. "
                          : !onThinks ? "the ON fragment has no effect. " : "the OFF fragment has no effect. ") + facts;
                break;
        }
        return r;
    }

    private static bool HasThink(string? s) =>
        s != null && (s.Contains("<think>", StringComparison.OrdinalIgnoreCase) || s.Contains("</think>", StringComparison.OrdinalIgnoreCase));
}
