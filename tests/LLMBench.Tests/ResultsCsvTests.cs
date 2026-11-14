using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Tests;

/// <summary>
/// Pre-test of the CSV export with simulated runs on the real catalog: planning, scoring of fake replies,
/// result files, leaderboard grouping and the CSV are the production code paths.
/// Set LLMBENCH_SAMPLE_OUT to a folder to keep the generated CSV + legend for inspection.
/// </summary>
public class ResultsCsvTests
{
    private static Workspace TempWorkspace()
    {
        var real = Workspace.Find();
        var dir = Path.Combine(Path.GetTempPath(), "llmbench-csv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "catalogs"));
        Directory.CreateDirectory(Path.Combine(dir, "prompts"));
        File.Copy(Path.Combine(real.CatalogsDir, "core-v5.json"), Path.Combine(dir, "catalogs", "core-v5.json"));
        foreach (var f in Directory.GetFiles(real.PromptsDir)) File.Copy(f, Path.Combine(dir, "prompts", Path.GetFileName(f)));
        return new Workspace(dir);
    }

    /// <summary>Writes a complete fake run. behaviour(index) returns "correct", "wrong", "nofinal" or "truncated".</summary>
    private static void FakeRun(Workspace ws, string name, string model, Func<int, string> behaviour)
    {
        var def = ws.LoadRunDefaults();
        def.Name = name;
        def.Model = model;
        var plan = RunPlanner.Build(ws, def);
        ws.Runs.Save(def);
        var qs = QuestionSet.FromPlan(plan);
        var header = new RunHeader
        {
            RunName = name, StartedAt = DateTime.Now, Definition = def.Clone(), ModelAlias = model, ModelPath = $"/models/{model}.gguf",
            CatalogFile = plan.Catalog.RelativePath, CatalogSha256 = plan.Catalog.Sha256, PlannedItems = plan.Items.Count,
            QuestionSetHash = qs.Hash, QuestionSetCount = qs.Count, ThinkingVerified = true,
        };
        var records = new List<ItemRecord>();
        using (var w = new ResultWriter(ws.Runs.ResultsPath(name)))
        {
            w.Write(header);
            foreach (var it in plan.Items)
            {
                var v = it.Variant;
                var mode = behaviour(it.Index);
                string answer = v.IsChoice
                    ? (mode == "wrong" ? it.DisplayedOptions.First(o => o.Id != v.CorrectOptionId).Letter : it.CorrectLetter!)
                    : (mode == "wrong" ? "999" : v.AcceptedAnswers[0]);
                var content = mode == "nofinal" ? "I think it is probably that one." : $"Some reasoning.\nFinal Answer: {answer}";
                var finish = mode == "truncated" ? "length" : "stop";
                var score = Scorer.Score(v, it.DisplayedOptions, mode == "truncated" ? "Some reasoning that never ends" : content, finish);
                var rec = new ItemRecord
                {
                    Index = it.Index, QuestionId = it.Question.Id, VariantId = v.Id, VariantType = v.IsChoice ? "choice" : "free",
                    VariantKind = v.Kind, AnswerType = v.IsChoice ? null : v.AnswerType, Category = it.Question.Category,
                    Tier = v.Tier ?? it.Question.Tier, Repetition = it.Repetition, DisplayedOptions = it.DisplayedOptions,
                    SystemMessage = it.SystemMessage, UserMessage = it.UserMessage, Expected = it.Expected,
                    CorrectOptionId = v.CorrectOptionId, CorrectLetter = it.CorrectLetter, AcceptedAnswers = v.IsChoice ? null : v.AcceptedAnswers,
                    Status = score.Status, Reason = score.Reason, ExtractedAnswer = score.ExtractedAnswer,
                    ChosenLetter = score.ChosenLetter, ChosenOptionId = score.ChosenOptionId, FinishReason = finish,
                };
                records.Add(rec);
                w.Write(rec);
            }
            var summary = SummaryBuilder.Build(records, plan.Items.Count);
            summary.Completed = true;
            w.Write(summary);
        }
    }

    [Fact]
    public void Csv_export_with_three_simulated_models()
    {
        var ws = TempWorkspace();
        try
        {
            FakeRun(ws, "std-model-a", "model-a", _ => "correct");
            FakeRun(ws, "std-model-b", "model-b", i => i % 10 == 0 ? "nofinal" : i % 3 == 0 ? "wrong" : "correct");
            FakeRun(ws, "std-model-c", "model-c", i => i % 7 == 0 ? "truncated" : "correct");

            var group = Leaderboard.Build(ws).Single();
            Assert.Equal(3, group.Entries.Count);                       // all three in one question set
            var runs = Leaderboard.AssignRanks(group.Entries.Where(e => e.RunName.StartsWith("std-")).ToList());
            var catalog = ResultsCsv.MatchingCatalog(ws, runs);
            Assert.NotNull(catalog);

            var csv = ResultsCsv.Build(runs, catalog);
            var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            Assert.Equal("question_id,variant_id,kind,format,tier,category,expected,answer_1,eval_1,answer_2,eval_2,answer_3,eval_3,question", lines[0]);
            Assert.Equal(1 + catalog!.VariantCount, lines.Count);    // one row per variant

            Assert.Equal("std-model-a", runs[0].RunName);                // sorted by accuracy
            var bo = lines.Single(l => l.StartsWith("CS-031,mcq,"));
            Assert.StartsWith("CS-031,mcq,original,choice,2,logic,Bo,Bo,correct,", bo);   // option text, not letter
            Assert.Contains(lines, l => l.Contains(",invalid:no-final-line,"));
            Assert.Contains(lines, l => l.Contains(",truncated,"));
            Assert.Contains(lines, l => l.StartsWith("CS-101,altered-free,altered,free,2,altered_classic,2,"));
            // question text last; contains commas -> quoted
            Assert.EndsWith("\"A farmer stands on the left bank of a river with a wolf, a goat and a cabbage. His boat is large enough to carry him and all three of them at the same time. What is the minimum number of river crossings needed to bring everything to the right bank?\"",
                            lines.Single(l => l.StartsWith("CS-001,mcq,")));

            var compact = ResultsCsv.Build(runs, catalog, includeQuestion: false);
            Assert.EndsWith(",eval_3", compact.Split('\n')[0].TrimEnd('\r'));

            var briefing = LlmBriefing.Build(runs, catalog, "results.csv", "results-legend.txt");
            Assert.Contains("## Catalog format", briefing);
            Assert.Contains("## How answers are scored", briefing);
            Assert.Contains("| 1 | model-a | 100.0 %", briefing);            // computed results table
            Assert.Contains("NEW-001", briefing);
            Assert.DoesNotContain("correctOptionId", briefing);             // no old catalog format

            var md = LlmExport.Build(ws, group, runs);
            Assert.Contains("\"base\"", md);                                  // new format example
            Assert.DoesNotContain("correctOptionId", md);

            var legend = ResultsCsv.BuildLegend(runs, "results.csv", catalog, true);
            Assert.Contains("1 = run 'std-model-a'", legend);
            Assert.Contains("invalid:<reason>", legend);

            var keep = Environment.GetEnvironmentVariable("LLMBENCH_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(keep))
            {
                Directory.CreateDirectory(keep);
                File.WriteAllText(Path.Combine(keep, "sample-results.csv"), csv, new System.Text.UTF8Encoding(false));
                File.WriteAllText(Path.Combine(keep, "sample-results-legend.txt"), legend);
                File.WriteAllText(Path.Combine(keep, "sample-results-briefing.md"), briefing);
            }
        }
        finally { Directory.Delete(ws.Root, true); }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "line1 line2")]
    public void Escape_follows_rfc4180_and_keeps_one_line(string input, string expected) =>
        Assert.Equal(expected, ResultsCsv.Escape(input));
}
