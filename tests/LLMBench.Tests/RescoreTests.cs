using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Tests;

public class RescoreTests
{
    [Fact]
    public void Rescore_applies_current_rules_to_stored_replies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmbench-rescore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "catalogs"));
        Directory.CreateDirectory(Path.Combine(dir, "prompts"));
        try
        {
            var ws = new Workspace(dir);
            var def = new RunDefinition { Name = "old-run" };
            ws.Runs.Save(def);
            // an item scored with the old rule: several numbers -> invalid:ambiguous
            var items = new List<ItemRecord>
            {
                new() { Index = 0, QuestionId = "Q1", VariantId = "free", VariantType = "free", AnswerType = "number",
                        AcceptedAnswers = ["5"], Expected = "5", UserMessage = "How many minutes?",
                        Content = "Final Answer: 5 minutes (since each machine takes 1 minute to make 1 widget)", FinishReason = "stop",
                        Status = ItemStatus.Invalid, Reason = "ambiguous", ExtractedAnswer = "5 minutes (since each machine takes 1 minute to make 1 widget)" },
                new() { Index = 1, QuestionId = "Q2", VariantId = "free", VariantType = "free", AnswerType = "number",
                        AcceptedAnswers = ["3"], Expected = "3", UserMessage = "How many?", Content = "Final Answer: 3", FinishReason = "stop",
                        Status = ItemStatus.Correct, ExtractedAnswer = "3" },
            };
            using (var w = new ResultWriter(ws.Runs.ResultsPath("old-run")))
            {
                w.Write(new RunHeader { RunName = "old-run", Definition = def, PlannedItems = 2, CatalogFile = "catalogs/x.json", CatalogSha256 = "x" });
                foreach (var i in items) w.Write(i);
                var s = SummaryBuilder.Build(items, 2);
                s.Completed = true;
                w.Write(s);
            }

            var dry = Rescorer.Rescore(ws, "old-run", dryRun: true);
            Assert.Single(dry.Changes);
            Assert.Equal(ItemStatus.Correct, dry.Changes[0].NewStatus);
            Assert.Null(dry.BackupFile);
            Assert.Equal(ItemStatus.Invalid, RunResults.Load(ws.Runs.ResultsPath("old-run")).Items[0].Status); // unchanged

            var done = Rescorer.Rescore(ws, "old-run", dryRun: false);
            Assert.True(File.Exists(done.BackupFile));
            var after = RunResults.Load(ws.Runs.ResultsPath("old-run"));
            Assert.Equal(ItemStatus.Correct, after.Items[0].Status);
            Assert.Equal(1.0, after.Summary!.Accuracy!.Value, 9);
            Assert.True(after.Summary.Completed);
            Assert.Single(after.Header!.Rescored);

            Assert.Empty(Rescorer.Rescore(ws, "old-run", dryRun: false).Changes); // idempotent
        }
        finally { Directory.Delete(dir, true); }
    }
}
