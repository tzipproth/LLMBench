using System.Text;
using LLMBench.Core.Catalog;

namespace LLMBench.Core.Runs;

/// <summary>
/// Identity of the questions a run asks: a hash over the content of every planned (question, variant):
/// text, options, solution, image. Runs with the same hash ask exactly the same questions, regardless of
/// system prompt, instruction, shuffling, thinking or sampling - so they can be ranked against each other.
/// </summary>
public static class QuestionSet
{
    public sealed record Identity(string Hash, int Count, bool Exact);

    public static Identity FromPlan(RunPlan plan)
    {
        var pairs = plan.Items
            .GroupBy(i => i.Key)
            .Select(g => (g.First().Question, g.First().Variant));
        return Compute(pairs);
    }

    public static Identity Compute(IEnumerable<(CatalogQuestion Q, QuestionVariant V)> pairs)
    {
        var lines = pairs
            .Select(p => Line(p.Q, p.V))
            .Distinct()
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
        return new Identity(Json.Sha256(string.Join("\n", lines))[..16], lines.Count, true);
    }

    private static string Line(CatalogQuestion q, QuestionVariant v)
    {
        var sb = new StringBuilder();
        sb.Append(q.Id).Append('\u001f').Append(v.Id).Append('\u001f').Append(v.Type).Append('\u001f').Append(v.AnswerType)
          .Append('\u001f').Append(v.Question).Append('\u001f').Append(v.Image);
        foreach (var o in v.Options) sb.Append('\u001f').Append(o.Id).Append('=').Append(o.Text);
        sb.Append('\u001f').Append(v.CorrectOptionId);
        foreach (var a in v.AcceptedAnswers) sb.Append('\u001f').Append(a);
        return sb.ToString();
    }

    /// <summary>
    /// Identity of an existing run: from the header (new runs), else by rebuilding the plan when the
    /// catalog file is unchanged, else an approximation from catalog hash + answered keys.
    /// </summary>
    public static Identity ForRun(Workspace ws, RunHeader header, IReadOnlyList<ItemRecord> items)
    {
        if (!string.IsNullOrEmpty(header.QuestionSetHash))
            return new Identity(header.QuestionSetHash, header.QuestionSetCount, true);
        try
        {
            var catalog = CatalogLoader.Load(ws, header.CatalogFile);
            if (catalog.IsValid && catalog.Sha256 == header.CatalogSha256)
            {
                var def = header.Definition.Clone();
                if (string.IsNullOrEmpty(def.Name)) def.Name = "x";
                return FromPlan(RunPlanner.Build(ws, def));
            }
        }
        catch (Exception ex) when (ex is BenchException or IOException) { }

        var keys = items.Select(i => i.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal);
        return new Identity("legacy-" + Json.Sha256(header.CatalogSha256 + "\n" + string.Join("\n", keys))[..9], items.Select(i => i.Key).Distinct().Count(), false);
    }
}
