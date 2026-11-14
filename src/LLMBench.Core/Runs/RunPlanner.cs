using LLMBench.Core.Catalog;
using LLMBench.Core.Prompting;

namespace LLMBench.Core.Runs;

public sealed class PlannedItem
{
    public required int Index { get; init; }
    public required CatalogQuestion Question { get; init; }
    public required QuestionVariant Variant { get; init; }
    public required int Repetition { get; init; }
    public required long? Seed { get; init; }
    public required List<DisplayedOption> DisplayedOptions { get; init; }
    public required string? SystemMessage { get; init; }
    public required string UserMessage { get; init; }

    public string? CorrectLetter => DisplayedOptions.FirstOrDefault(d => d.Id == Variant.CorrectOptionId)?.Letter;

    public string Expected => Variant.IsChoice
        ? $"{CorrectLetter}. {Variant.ExpectedText}"
        : Variant.ExpectedText;

    public string Key => $"{Question.Id}/{Variant.Id}";
}

public sealed record SkippedEntry(string QuestionId, string? VariantId, string Reason);

public sealed class RunPlan
{
    public required QuestionCatalog Catalog { get; init; }
    public required string? SystemPromptFile { get; init; }
    public required string? SystemPrompt { get; init; }
    public required List<PlannedItem> Items { get; init; }
    public required List<SkippedEntry> Skipped { get; init; }
}

/// <summary>Turns a run definition and a catalog into the ordered list of requests.</summary>
public static class RunPlanner
{
    // tier filter on the effective tier of each variant (a variant may override the question's tier)
    private static bool TierOk(RunDefinition def, QuestionVariant v) =>
        def.Tiers.Count == 0 || (v.Tier.HasValue && def.Tiers.Contains(v.Tier.Value));

    public static RunPlan Build(Workspace ws, RunDefinition def)
    {
        var problems = def.Check();
        if (problems.Count > 0) throw new BenchException("Invalid run parameters:\n  " + string.Join("\n  ", problems));

        var catalog = CatalogLoader.LoadValid(ws, def.CatalogFile);

        string? systemPrompt = null;
        if (!string.IsNullOrWhiteSpace(def.SystemPromptFile))
        {
            var p = ws.Resolve(def.SystemPromptFile);
            if (!File.Exists(p)) throw new BenchException($"System prompt file not found: {def.SystemPromptFile}");
            systemPrompt = File.ReadAllText(p).Trim();
            if (systemPrompt.Length == 0) systemPrompt = null;
        }
        // With no answer instruction the system prompt alone must ask for the answer line, otherwise nothing can be scored.
        if (string.Equals(def.AnswerInstruction, Prompting.AnswerInstructions.None, StringComparison.OrdinalIgnoreCase) &&
            (systemPrompt == null || !systemPrompt.Contains("final answer", StringComparison.OrdinalIgnoreCase)))
            throw new BenchException("Answer instruction is 'none', but the system prompt does not ask for a \"Final Answer:\" line - no reply could be scored. " +
                                     "Use a system prompt that contains it (e.g. prompts/neutral.txt) or the answer instruction 'reasoned'.");

        var skipped = new List<SkippedEntry>();
        IEnumerable<CatalogQuestion> questions = catalog.Questions;
        if (def.QuestionIds.Count > 0)
            questions = questions.Where(q => def.QuestionIds.Contains(q.Id, StringComparer.OrdinalIgnoreCase));
        if (def.Categories.Count > 0)
            questions = questions.Where(q => q.Category != null && def.Categories.Contains(q.Category, StringComparer.OrdinalIgnoreCase));
        if (def.Limit > 0) questions = questions.Take(def.Limit);
        var selected = questions.ToList();

        // (question, variant) pairs in catalog order
        var pairs = new List<(CatalogQuestion Q, QuestionVariant V)>();
        foreach (var q in selected)
        {
            if (def.AllVariants)
            {
                pairs.AddRange(q.Variants.Where(v => TierOk(def, v)).Select(v => (q, v)));
                continue;
            }
            foreach (var name in def.Variants)
            {
                var v = q.Variants.FirstOrDefault(x => string.Equals(x.Id, name, StringComparison.OrdinalIgnoreCase));
                if (v != null) { if (TierOk(def, v)) pairs.Add((q, v)); }
                else skipped.Add(new SkippedEntry(q.Id, name, "variant-missing"));
            }
        }

        // repetition is the outer loop: an aborted run still covers every question of the first repetition
        var items = new List<PlannedItem>();
        for (int rep = 0; rep < def.Repetitions; rep++)
        {
            foreach (var (q, v) in pairs)
            {
                var displayed = v.IsChoice
                    ? PromptBuilder.ArrangeOptions(q.Id, v, def.ShuffleOptions, def.ShuffleSeed, rep)
                    : [];
                var user = PromptBuilder.BuildUserMessage(v, displayed, def.AnswerInstruction);
                var (sys, usr) = PromptBuilder.ApplyPlacement(systemPrompt, def.SystemPromptPlacement, user);
                items.Add(new PlannedItem
                {
                    Index = items.Count,
                    Question = q,
                    Variant = v,
                    Repetition = rep,
                    Seed = def.Seed.HasValue ? def.Seed.Value + rep : null,
                    DisplayedOptions = displayed,
                    SystemMessage = sys,
                    UserMessage = usr,
                });
            }
        }

        if (pairs.Count == 0 && selected.Count > 0 && def.Tiers.Count > 0)
            skipped.Add(new SkippedEntry("*", null, "no variant matches the tier filter"));

        return new RunPlan
        {
            Catalog = catalog,
            SystemPromptFile = def.SystemPromptFile,
            SystemPrompt = systemPrompt,
            Items = items,
            Skipped = skipped,
        };
    }
}
