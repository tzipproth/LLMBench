using System.Globalization;
using System.Text;
using LLMBench.Core.Catalog;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Stats;

/// <summary>
/// Compact results table: one row per asked question variant, one answer + evaluation column pair per run,
/// the question text last (for humans; the catalog holds the full question). Plus a short legend file.
/// </summary>
public static class ResultsCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string EvaluationHelp =
        "correct = right answer; incorrect = clear but wrong answer; invalid:<reason> = no usable final answer " +
        "(no-final-line = no 'Final Answer:' line, ambiguous = several answers, out-of-range = letter not offered, " +
        "unparseable = not a letter/option, no-number = no number in a number answer, empty = empty reply); " +
        "truncated = reply cut off at the token limit; error = server/HTTP error (not counted); skipped = not asked (not counted). " +
        "With repetitions: answers joined by '|', evaluation = correct/total.";

    /// <param name="runs">Runs in column order (model 1, 2, ...).</param>
    /// <param name="catalog">Catalog for row order and question texts (optional; falls back to the result files).</param>
    public static string Build(IReadOnlyList<LeaderboardEntry> runs, QuestionCatalog? catalog, bool includeQuestion = true)
    {
        var sb = new StringBuilder();
        var header = new List<string> { "question_id", "variant_id", "kind", "format", "tier", "category", "expected" };
        for (int i = 1; i <= runs.Count; i++) { header.Add($"answer_{i}"); header.Add($"eval_{i}"); }
        if (includeQuestion) header.Add("question");
        sb.AppendLine(string.Join(",", header));

        foreach (var key in RowKeys(runs, catalog))
        {
            var sample = runs.SelectMany(r => r.Items).First(i => i.Key == key);
            var variant = catalog?.Questions.FirstOrDefault(q => q.Id == sample.QuestionId)?.Variants.FirstOrDefault(v => v.Id == sample.VariantId);
            var cells = new List<string>
            {
                sample.QuestionId,
                sample.VariantId,
                sample.VariantKind ?? (sample.VariantId == "control" ? "original" : ""),
                sample.VariantType,
                sample.Tier?.ToString(Inv) ?? "",
                sample.Category ?? "",
                ExpectedText(sample),
            };
            foreach (var run in runs)
            {
                var items = run.Items.Where(i => i.Key == key).OrderBy(i => i.Repetition).ToList();
                if (items.Count == 0) { cells.Add(""); cells.Add("not-asked"); continue; }
                if (items.Count == 1) { cells.Add(AnswerText(items[0])); cells.Add(Evaluation(items[0])); continue; }
                cells.Add(string.Join("|", items.Select(AnswerText)));
                var scored = items.Where(SummaryBuilder.IsScored).ToList();
                cells.Add($"{scored.Count(i => i.Status == ItemStatus.Correct)}/{scored.Count}");
            }
            if (includeQuestion) cells.Add(variant?.Question ?? QuestionFromPrompt(sample));
            sb.AppendLine(string.Join(",", cells.Select(Escape)));
        }
        return sb.ToString();
    }

    public static string BuildLegend(IReadOnlyList<LeaderboardEntry> runs, string csvFileName, QuestionCatalog? catalog, bool includeQuestion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LLMBench results export – legend for {csvFileName}");
        sb.AppendLine($"Created {DateTime.Now:yyyy-MM-dd HH:mm} with LLMBench {BenchRunner.Version}.");
        if (runs.Count > 0)
        {
            var h = runs[0].Header;
            sb.AppendLine($"Catalog: {h.CatalogFile} (sha256 {h.CatalogSha256[..Math.Min(12, h.CatalogSha256.Length)]}), question set {runs[0].QuestionSetHash}" +
                          (catalog == null ? " – catalog file changed or missing, question texts taken from the result files" : ""));
        }
        sb.AppendLine();
        sb.AppendLine("Models (column numbers):");
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            var d = r.Definition;
            sb.AppendLine($"  {i + 1} = run '{r.RunName}': model {r.Model} ({r.ModelFile}), thinking {d.Thinking}, system prompt {r.SystemPrompt}, " +
                          $"instruction {d.AnswerInstruction}, temperature {d.Temperature?.ToString("0.##", Inv) ?? "server default"}, repetitions {d.Repetitions} – " +
                          $"accuracy {TextReport.Pct(r.Summary.Accuracy)} ({r.Summary.Correct}/{r.Summary.Scored}), {RunTiming.Line(r.Summary)}");
        }
        sb.AppendLine();
        sb.AppendLine("Columns:");
        sb.AppendLine("  question_id, variant_id  reference to the catalog (question id / variant id)");
        sb.AppendLine("  kind                     original = base question; altered = detail changed so a memorized answer is wrong;");
        sb.AppendLine("                           distractor = irrelevant sentence added. Original + altered of one question = control pair.");
        sb.AppendLine("  format                   choice = multiple choice, free = no options (the model had to find the answer)");
        sb.AppendLine("  tier                     1 = easy anchor, 2 = medium, 3 = hard, 4 = very hard, 5 = hardest, 6 = room puzzles, 7 = last hard round, 8 = object-frame round");
        sb.AppendLine("  category                 topic group of the question");
        sb.AppendLine("  expected                 correct answer (option text, or accepted answers separated by '|')");
        sb.AppendLine("  answer_N                 answer of model N (option text for multiple choice; empty = no answer found)");
        sb.AppendLine("  eval_N                   evaluation of answer_N (see below)");
        if (includeQuestion) sb.AppendLine("  question                 question text as asked (options are in the catalog)");
        sb.AppendLine();
        sb.AppendLine("Evaluation values:");
        sb.AppendLine("  " + EvaluationHelp);
        return sb.ToString();
    }

    /// <summary>The catalog file of the runs, if it is unchanged since all of them ran (else null).</summary>
    public static QuestionCatalog? MatchingCatalog(Workspace ws, IReadOnlyList<LeaderboardEntry> runs)
    {
        if (runs.Count == 0) return null;
        try
        {
            var c = CatalogLoader.Load(ws, runs[0].Header.CatalogFile);
            return c.IsValid && runs.All(r => r.Header.CatalogSha256 == c.Sha256) ? c : null;
        }
        catch (BenchException) { return null; }
    }

    private static IEnumerable<string> RowKeys(IReadOnlyList<LeaderboardEntry> runs, QuestionCatalog? catalog)
    {
        var present = runs.SelectMany(r => r.Items).Select(i => i.Key).ToHashSet();
        var ordered = new List<string>();
        if (catalog != null)
            ordered.AddRange(catalog.Questions.SelectMany(q => q.Variants.Select(v => $"{q.Id}/{v.Id}")).Where(present.Contains));
        // keys not in the catalog (changed catalog): in the order of the first run that has them
        ordered.AddRange(runs.SelectMany(r => r.Items.OrderBy(i => i.Index)).Select(i => i.Key).Distinct().Where(k => !ordered.Contains(k)));
        return ordered;
    }

    public static string ExpectedText(ItemRecord i) =>
        i.VariantType == "choice"
            ? i.DisplayedOptions.FirstOrDefault(o => o.Id == i.CorrectOptionId)?.Text ?? i.Expected
            : string.Join("|", i.AcceptedAnswers ?? [i.Expected]);

    public static string AnswerText(ItemRecord i)
    {
        if (i.Status is ItemStatus.Error or ItemStatus.Skipped) return "";
        if (i.ChosenOptionId != null && i.DisplayedOptions.FirstOrDefault(o => o.Id == i.ChosenOptionId) is { } o) return o.Text;
        return i.ExtractedAnswer ?? "";
    }

    public static string Evaluation(ItemRecord i) => i.Status switch
    {
        ItemStatus.Invalid => "invalid:" + (i.Reason ?? "?"),
        _ => i.Status.ToString().ToLowerInvariant(),
    };

    // first paragraph of the user message = the question (options and answer instruction follow after a blank line)
    private static string QuestionFromPrompt(ItemRecord i)
    {
        var parts = i.UserMessage.Replace("\r\n", "\n").Split("\n\n");
        return parts.Length > 0 ? parts[0] : i.UserMessage;
    }

    /// <summary>RFC 4180 field; line breaks become spaces so that every row stays on one line.</summary>
    public static string Escape(string? value)
    {
        var v = (value ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return v.IndexOfAny([',', '"']) >= 0 || v.StartsWith(' ') || v.EndsWith(' ')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}
