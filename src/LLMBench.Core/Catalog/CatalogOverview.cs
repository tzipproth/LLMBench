using System.Text;
using LLMBench.Core.Prompting;

namespace LLMBench.Core.Catalog;

/// <summary>Readable Markdown overview of a catalog: counts, and every question with base and variants.</summary>
public static class CatalogOverview
{
    public static string ToMarkdown(QuestionCatalog c)
    {
        var sb = new StringBuilder();
        var variants = c.Questions.SelectMany(q => q.Variants).ToList();
        sb.AppendLine($"# Catalog {Path.GetFileName(c.RelativePath)} – overview");
        sb.AppendLine();
        sb.AppendLine($"{c.Questions.Count} questions, {variants.Count} variants. Each question has a **base** (main question, answer list, answer) " +
                      "and the **variants** that are actually asked; every variant is sent to the model as its own request. " +
                      $"Catalog sha256 `{c.Sha256[..12]}`.");
        sb.AppendLine();
        if (!c.IsValid)
        {
            sb.AppendLine("**The catalog has errors:**");
            foreach (var e in c.Errors) sb.AppendLine($"- {e}");
            sb.AppendLine();
        }

        var cats = c.Questions.Select(q => q.Category ?? "-").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        sb.AppendLine("## Questions by category and tier (tier of the question)");
        sb.AppendLine();
        var tiers = Enumerable.Range(1, CatalogLoader.MaxTier).ToList();
        sb.AppendLine("| Category | " + string.Join(" | ", tiers.Select(t => $"Tier {t}")) + " | Questions | Variants |");
        sb.AppendLine("|---|" + string.Concat(tiers.Select(_ => "---|")) + "---|---|");
        foreach (var k in cats)
        {
            var qs = c.Questions.Where(q => (q.Category ?? "-") == k).ToList();
            sb.AppendLine($"| {k} | " + string.Join(" | ", tiers.Select(t => qs.Count(q => q.Tier == t))) + $" | {qs.Count} | {qs.Sum(q => q.Variants.Count)} |");
        }
        sb.AppendLine("| **total** | " + string.Join(" | ", tiers.Select(t => c.Questions.Count(q => q.Tier == t))) + $" | {c.Questions.Count} | {variants.Count} |");
        sb.AppendLine();

        sb.AppendLine("## Variants");
        sb.AppendLine();
        sb.AppendLine("| | Count |");
        sb.AppendLine("|---|---|");
        foreach (var g in variants.GroupBy(v => v.Kind).OrderBy(g => g.Key)) sb.AppendLine($"| kind `{g.Key}` | {g.Count()} |");
        sb.AppendLine($"| format multiple choice | {variants.Count(v => v.IsChoice)} |");
        sb.AppendLine($"| format free answer (number) | {variants.Count(v => !v.IsChoice && v.IsNumber)} |");
        sb.AppendLine($"| format free answer (text) | {variants.Count(v => !v.IsChoice && !v.IsNumber)} |");
        foreach (var g in variants.Where(v => v.IsChoice).GroupBy(v => v.Options.Count).OrderBy(g => g.Key))
            sb.AppendLine($"| multiple choice with {g.Key} options | {g.Count()} |");
        foreach (var g in variants.GroupBy(v => v.Tier).OrderBy(g => g.Key)) sb.AppendLine($"| effective tier {g.Key?.ToString() ?? "-"} | {g.Count()} |");
        sb.AppendLine($"| questions with both multiple choice and free answer | {c.Questions.Count(q => q.Variants.Any(v => v.IsChoice) && q.Variants.Any(v => !v.IsChoice))} |");
        sb.AppendLine($"| questions with original + altered (control pair) | {c.Questions.Count(q => q.Variants.Any(v => v.Kind == "original") && q.Variants.Any(v => v.Kind == "altered"))} |");
        sb.AppendLine();
        sb.AppendLine("Kinds: `original` = the base question as it is, `altered` = one detail changed so that the memorized answer of a well-known puzzle becomes wrong, " +
                      "`distractor` = an irrelevant sentence added. Original + altered of the same question form a control pair: altered wrong but original right means the model only recognizes the known puzzle.");
        sb.AppendLine();

        foreach (var k in cats)
        {
            sb.AppendLine($"## {k}");
            sb.AppendLine();
            foreach (var q in c.Questions.Where(q => (q.Category ?? "-") == k).OrderBy(q => q.Id, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {q.Id} — tier {q.Tier?.ToString() ?? "-"}{(q.Tags.Count > 0 ? " — " + string.Join(", ", q.Tags) : "")}");
                sb.AppendLine();
                sb.AppendLine("**Base**");
                sb.AppendLine();
                sb.AppendLine($"> {q.Base.Question.Replace("\n", "\n> ")}");
                sb.AppendLine();
                if (q.Base.Options.Count > 0)
                    sb.AppendLine("Answer list: " + string.Join(" · ", q.Base.Options.Select(o => string.Equals(o, q.Base.Answer, StringComparison.OrdinalIgnoreCase) ? $"**{o}** ✔" : o)) +
                                  (q.Base.FixedOrder ? " (fixed order)" : ""));
                else sb.AppendLine($"Answer: **{q.Base.Answer}**" + (q.Base.AnswerType != null ? $" ({q.Base.AnswerType})" : ""));
                if (q.Base.AcceptedAnswers.Count > 0) sb.AppendLine($"Also accepted (free answer): {string.Join(" | ", q.Base.AcceptedAnswers)}");
                if (q.Base.Note != null) sb.AppendLine($"\n*Note: {q.Base.Note}*");
                sb.AppendLine();
                sb.AppendLine("**Variants asked**");
                sb.AppendLine();
                foreach (var (v, spec) in q.Variants.Zip(q.VariantSpecs))
                {
                    var format = v.IsChoice ? $"multiple choice, {v.Options.Count} options" : $"free answer ({v.AnswerType})";
                    var tier = v.Tier != q.Tier ? $", tier {v.Tier}" : "";
                    sb.Append($"- `{v.Id}` — {v.Kind}, {format}{tier}");
                    if (spec.Question == null && spec.Answer == null && spec.Options == null)
                    {
                        sb.AppendLine(v.IsChoice ? "" : $" — answer: {string.Join(" | ", v.AcceptedAnswers)}");
                        continue;
                    }
                    sb.AppendLine(":");
                    if (spec.Question != null) sb.AppendLine($"  > {v.Question.Replace("\n", "\n  > ")}");
                    if (v.IsChoice)
                        sb.AppendLine("  options: " + string.Join(" · ", v.Options.Select((o, i) => $"{PromptBuilder.Letter(i)}. {o.Text}{(o.Id == v.CorrectOptionId ? " ✔" : "")}")));
                    else sb.AppendLine($"  answer: {string.Join(" | ", v.AcceptedAnswers)}");
                    if (spec.Note != null) sb.AppendLine($"  *note: {spec.Note}*");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
