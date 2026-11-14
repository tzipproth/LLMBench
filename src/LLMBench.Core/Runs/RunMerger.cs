using System.Text.Json;
using System.Text.Json.Nodes;
using LLMBench.Core.Llama;
using LLMBench.Core.Stats;

namespace LLMBench.Core.Runs;

/// <summary>
/// Combines finished runs of one model on parts of a catalog (e.g. core-v4 + core-v3_tier9) into one run on the whole
/// catalog (core-v5), exactly as if that catalog had been run in one go. This holds because every request depends only
/// on the run parameters and the question (option order: question id + shuffle seed; seed: per repetition), not on the
/// other questions of the run. The merge checks this: every planned request of the new catalog must exist in exactly
/// one source run with the identical request body; nothing may be left over.
/// </summary>
public static class RunMerger
{
    public static RunSummary Merge(Workspace ws, string newName, string catalogFile, IReadOnlyList<string> sourceNames)
    {
        if (sourceNames.Count < 2) throw new BenchException("Merge needs at least two source runs.");
        if (Directory.Exists(ws.Runs.RunDir(newName))) throw new BenchException($"Run '{newName}' already exists.");

        var sources = sourceNames.Select(n => (Name: n, Res: RunResults.Load(ws.Runs.ResultsPath(n)))).ToList();
        foreach (var (n, r) in sources)
        {
            if (r.Header == null) throw new BenchException($"Run '{n}' has no results.");
            if (r.Summary is not { Completed: true }) throw new BenchException($"Run '{n}' is not completed.");
            if (r.ReadErrors.Count > 0) throw new BenchException($"Run '{n}': results file has read errors ({r.ReadErrors[0]}).");
        }

        // same parameters apart from name and catalog, same model file
        var first = sources[0].Res.Header!;
        string Params(RunHeader h) { var t = h.Definition.AsTemplate(); t.CatalogFile = ""; return Json.Serialize(t); }
        foreach (var (n, r) in sources.Skip(1))
        {
            if (Params(r.Header!) != Params(first)) throw new BenchException($"Run '{n}' has other parameters than '{sources[0].Name}'.");
            if (r.Header!.ModelPath != first.ModelPath) throw new BenchException($"Run '{n}' used another model file ({r.Header.ModelPath}) than '{sources[0].Name}' ({first.ModelPath}).");
        }

        var def = first.Definition.Clone();
        def.Name = newName;
        def.CatalogFile = catalogFile;
        def.CreatedAt = DateTime.Now;
        def.RestartOf = null;
        def.Description = "Merged from " + string.Join(" + ", sourceNames);
        var plan = RunPlanner.Build(ws, def);

        var pool = new Dictionary<(string Key, int Rep), ItemRecord>();
        foreach (var (n, r) in sources)
            foreach (var item in r.Items)
                if (!pool.TryAdd((item.Key, item.Repetition), item))
                    throw new BenchException($"{item.Key} (repetition {item.Repetition}) is in more than one source run.");

        var items = new List<ItemRecord>();
        foreach (var p in plan.Items)
        {
            if (!pool.Remove((p.Key, p.Repetition), out var src))
                throw new BenchException($"{p.Key} (repetition {p.Repetition}) of {catalogFile} is in none of the source runs.");
            var expected = RequestBuilder.ForLog(RequestBuilder.Build(def, p.SystemMessage, p.UserMessage, p.Seed));
            if (p.Variant.Image != null || src.Request == null || !JsonNode.DeepEquals(src.Request, expected))
                throw new BenchException($"{p.Key}: the stored request differs from the one {catalogFile} would send.");
            var v = p.Variant;
            if (!src.DisplayedOptions.SequenceEqual(p.DisplayedOptions) || src.CorrectOptionId != (v.IsChoice ? v.CorrectOptionId : null) ||
                !(src.AcceptedAnswers ?? []).SequenceEqual(v.IsChoice ? [] : v.AcceptedAnswers))
                throw new BenchException($"{p.Key}: options or solution differ from {catalogFile}.");

            var rec = JsonSerializer.Deserialize<ItemRecord>(JsonSerializer.Serialize(src, Json.Compact), Json.Compact)!;
            rec.Index = p.Index;
            rec.VariantKind = v.Kind;
            rec.Category = p.Question.Category;
            rec.Tier = v.Tier ?? p.Question.Tier;
            rec.Tags = p.Question.Tags;
            rec.Expected = p.Expected;
            rec.CorrectLetter = p.CorrectLetter;
            items.Add(rec);
        }
        if (pool.Count > 0)
            throw new BenchException($"{pool.Count} item(s) of the source runs are not in {catalogFile}, e.g. {pool.Keys.First().Key}.");

        var qs = QuestionSet.FromPlan(plan);
        var header = JsonSerializer.Deserialize<RunHeader>(JsonSerializer.Serialize(first, Json.Compact), Json.Compact)!;
        header.RunName = newName;
        header.RunId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        header.StartedAt = sources.Min(s => s.Res.Header!.StartedAt);
        header.Definition = def.Clone();
        header.CatalogFile = plan.Catalog.RelativePath;
        header.CatalogSha256 = plan.Catalog.Sha256;
        header.CatalogQuestions = plan.Catalog.Questions.Count;
        header.PlannedItems = plan.Items.Count;
        header.QuestionSetHash = qs.Hash;
        header.QuestionSetCount = qs.Count;
        header.SkippedVariants = plan.Skipped;
        header.Warnings = sources.SelectMany(s => s.Res.Header!.Warnings).Distinct().ToList();
        header.Rescored = sources.SelectMany(s => s.Res.Header!.Rescored).ToList();
        header.MergedFrom = [.. sourceNames];

        var summary = SummaryBuilder.Build(items, plan.Items.Count);
        summary.Completed = true;
        summary.FinishedAt = sources.Max(s => s.Res.Summary!.FinishedAt);
        summary.DurationSeconds = sources.Sum(s => s.Res.Summary!.DurationSeconds);

        ws.Runs.Save(def);
        using (var w = new ResultWriter(ws.Runs.ResultsPath(newName)))
        {
            w.Write(header);
            foreach (var item in items) w.Write(item);
            w.Write(summary);
        }
        return summary;
    }
}
