using System.Text.Json;
using System.Text.Json.Nodes;
using LLMBench.Core.Runs;

namespace LLMBench.Core.Stats;

public sealed record ParameterDifference(string Name, string? A, string? B);

/// <summary>Flattens run parameters (plus server facts from the header) to compare runs.</summary>
public static class ParameterDiff
{
    private static readonly HashSet<string> AlwaysIgnored = ["name", "description", "createdAt", "restartOf", "schemaVersion"];

    // irrelevant for ranking runs that ask the same questions
    private static readonly HashSet<string> RankingIgnored =
        ["serverUrl", "serverNotes", "timeoutSeconds", "catalogFile", "variants", "categories", "tiers", "questionIds", "limit", "expectedContextSize"];

    public static SortedDictionary<string, string> Flatten(RunDefinition d, RunHeader? h, bool forRanking)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var node = JsonSerializer.SerializeToNode(d, Json.Compact) as JsonObject ?? [];
        foreach (var (key, value) in node)
        {
            if (AlwaysIgnored.Contains(key) || (forRanking && RankingIgnored.Contains(key))) continue;
            // the thinking fragments are covered by "thinking"; only a non-standard active fragment is listed
            if (key is "thinkingOn" or "thinkingOff") continue;
            result[key] = value?.ToJsonString(Json.Compact) ?? "null";
        }
        var standard = new RunDefinition();
        var active = d.Thinking switch { "on" => (d.ThinkingOn, standard.ThinkingOn), "off" => (d.ThinkingOff, standard.ThinkingOff), _ => default };
        if (active.Item1 != null && active.Item1.ToJsonString() != active.Item2!.ToJsonString())
            result["thinkingFragment"] = active.Item1.ToJsonString();
        if (h != null)
        {
            result["server.modelPath"] = h.ModelPath ?? "?";
            result["server.contextSize"] = h.ContextSize?.ToString() ?? "?";
            result["server.build"] = h.ServerProps?["build_info"]?.ToString() ?? "?";
            if (!forRanking) result["server.modelAlias"] = h.ModelAlias ?? "?";
        }
        return result;
    }

    public static List<ParameterDifference> Diff(RunDefinition a, RunHeader? ha, RunDefinition b, RunHeader? hb, bool forRanking = false)
    {
        var fa = Flatten(a, ha, forRanking);
        var fb = Flatten(b, hb, forRanking);
        return fa.Keys.Union(fb.Keys).OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new ParameterDifference(k, fa.GetValueOrDefault(k), fb.GetValueOrDefault(k)))
            .Where(d => d.A != d.B)
            .ToList();
    }

    /// <summary>Names of parameters whose values are not identical across all runs.</summary>
    public static List<string> Varying(IEnumerable<(RunDefinition Def, RunHeader? Header)> runs)
    {
        var flats = runs.Select(r => Flatten(r.Def, r.Header, forRanking: true)).ToList();
        if (flats.Count < 2) return [];
        return flats.SelectMany(f => f.Keys).Distinct()
            .Where(k => flats.Select(f => f.GetValueOrDefault(k)).Distinct().Count() > 1)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }
}
