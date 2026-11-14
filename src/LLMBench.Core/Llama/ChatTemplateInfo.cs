using System.Text.RegularExpressions;

namespace LLMBench.Core.Llama;

/// <summary>
/// What the model's chat template (from /props) does with thinking switches. llama-server only passes
/// <c>chat_template_kwargs</c> (and <c>--reasoning-effort</c>) on to the template; a switch has an effect only if the
/// template reads that variable. This is a text analysis of the Jinja template, not a guarantee.
/// </summary>
public sealed partial class ChatTemplateInfo
{
    public bool HasTemplate { get; init; }

    /// <summary>The template reads <c>enable_thinking</c> (thinking on/off via chat_template_kwargs).</summary>
    public bool EnableThinking { get; init; }

    /// <summary>Thinking is on when <c>enable_thinking</c> is not set (true / false / null = not recognisable).</summary>
    public bool? ThinkingDefaultOn { get; init; }

    /// <summary>The template reads <c>reasoning_effort</c>.</summary>
    public bool ReasoningEffort { get; init; }

    /// <summary>Effort values the template compares against (sorted low to high where known).</summary>
    public List<string> EffortValues { get; init; } = [];

    /// <summary>Effort used when none is given (from <c>reasoning_effort|default('...')</c>), or null.</summary>
    public string? EffortDefault { get; init; }

    /// <summary>Thinking markers found in the template, e.g. &lt;think&gt;.</summary>
    public List<string> Markers { get; init; } = [];

    private static readonly string[] KnownMarkers = ["<think>", "<|think|>", "<|channel|>", "<thinking>", "<|start_thinking|>", "[THINK]"];
    private static readonly string[] EffortOrder = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static ChatTemplateInfo Analyze(string? template)
    {
        if (string.IsNullOrEmpty(template)) return new ChatTemplateInfo { HasTemplate = false };

        var enable = template.Contains("enable_thinking", StringComparison.Ordinal);
        bool? defaultOn = null;
        if (enable)
        {
            if (DefaultOnRegex().IsMatch(template)) defaultOn = true;
            else if (DefaultOffRegex().IsMatch(template)) defaultOn = false;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in EffortCompareRegex().Matches(template)) values.Add(m.Groups["v"].Value);
        foreach (Match m in EffortInRegex().Matches(template))
            foreach (Match s in QuotedRegex().Matches(m.Groups["list"].Value)) values.Add(s.Groups[1].Value);
        var def = EffortDefaultRegex().Match(template) is { Success: true } d ? d.Groups["v"].Value : null;
        if (def != null) values.Add(def);

        var order = values.OrderBy(v => Array.IndexOf(EffortOrder, v) is var i && i < 0 ? 99 : i).ThenBy(v => v, StringComparer.Ordinal).ToList();
        return new ChatTemplateInfo
        {
            HasTemplate = true,
            EnableThinking = enable,
            ThinkingDefaultOn = defaultOn,
            ReasoningEffort = template.Contains("reasoning_effort", StringComparison.Ordinal),
            EffortValues = order,
            EffortDefault = def,
            Markers = KnownMarkers.Where(k => template.Contains(k, StringComparison.Ordinal)).ToList(),
        };
    }

    /// <summary>One line for the server check, e.g. "enable_thinking (default on) | reasoning_effort: low, medium, xhigh (default xhigh) | &lt;think&gt;".</summary>
    public string Summary()
    {
        if (!HasTemplate) return "no chat template in /props";
        var parts = new List<string>();
        parts.Add(EnableThinking
            ? "enable_thinking" + (ThinkingDefaultOn switch { true => " (default on)", false => " (default off)", _ => "" })
            : "no enable_thinking");
        if (ReasoningEffort)
            parts.Add("reasoning_effort" + (EffortValues.Count > 0 ? ": " + string.Join(", ", EffortValues) : "") +
                      (EffortDefault != null ? $" (default {EffortDefault})" : ""));
        else parts.Add("no reasoning_effort");
        if (Markers.Count > 0) parts.Add(string.Join(" ", Markers));
        return string.Join(" | ", parts);
    }

    [GeneratedRegex(@"enable_thinking\s+if\s+enable_thinking\s+is\s+defined\s+else\s+[Tt]rue|enable_thinking\s+is\s+(undefined|not\s+defined)\s+or\s+enable_thinking|enable_thinking\s*\|\s*default\(\s*true|enable_thinking\s+is\s+defined\s+and\s+enable_thinking\s+is\s+false")]
    private static partial Regex DefaultOnRegex();

    [GeneratedRegex(@"enable_thinking\s+if\s+enable_thinking\s+is\s+defined\s+else\s+[Ff]alse|enable_thinking\s*\|\s*default\(\s*false|enable_thinking\s+is\s+defined\s+and\s+enable_thinking(\s+is\s+true)?\s*%")]
    private static partial Regex DefaultOffRegex();

    [GeneratedRegex(@"\w*effort\w*\s*(==|!=)\s*['""](?<v>[A-Za-z]+)['""]")]
    private static partial Regex EffortCompareRegex();

    [GeneratedRegex(@"\w*effort\w*\s+(not\s+)?in\s*[\(\[](?<list>[^\)\]]*)[\)\]]")]
    private static partial Regex EffortInRegex();

    [GeneratedRegex(@"['""]([A-Za-z]+)['""]")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"reasoning_effort\s*\|\s*default\(\s*['""](?<v>[A-Za-z]+)['""]")]
    private static partial Regex EffortDefaultRegex();
}
