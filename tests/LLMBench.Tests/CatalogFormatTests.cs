using LLMBench.Core;
using LLMBench.Core.Catalog;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Tests;

public class CatalogFormatTests
{
    private static CatalogQuestion Sally() => new()
    {
        Id = "CS-101", Category = "altered_classic", Tier = 2,
        Base = new QuestionBase
        {
            Question = "Sally has 3 brothers. Each of her brothers has 2 sisters. How many sisters does Sally have?",
            AnswerType = "number",
            Options = ["0", "1", "2", "3", "6", "cannot be determined"],
            Answer = "1",
        },
        VariantSpecs =
        [
            new VariantSpec { Id = "free", Format = "free", Tier = 1 },
            new VariantSpec { Id = "mcq", Format = "choice" },
            new VariantSpec { Id = "altered-mcq", Kind = "altered", Format = "choice", OptionCount = 4,
                              Question = "Sally has 3 brothers. Each of her brothers has 3 sisters. How many sisters does Sally have?", Answer = "2" },
        ],
    };

    private static List<QuestionVariant> Resolve(CatalogQuestion q, out List<string> errors)
    {
        errors = [];
        CatalogLoader.Resolve(q, errors);
        return q.Variants;
    }

    [Fact]
    public void Variants_inherit_from_the_base()
    {
        var v = Resolve(Sally(), out var errors);
        Assert.Empty(errors);
        Assert.Equal(3, v.Count);

        Assert.False(v[0].IsChoice);
        Assert.Equal(["1"], v[0].AcceptedAnswers);
        Assert.Equal("number", v[0].AnswerType);
        Assert.Equal(1, v[0].Tier);           // override
        Assert.Equal("original", v[0].Kind);

        Assert.True(v[1].IsChoice);
        Assert.Equal(6, v[1].Options.Count);
        Assert.Equal("1", v[1].ExpectedText);
        Assert.Equal(2, v[1].Tier);           // inherited
    }

    [Fact]
    public void OptionCount_keeps_the_correct_answer_and_the_first_distractors()
    {
        var alt = Resolve(Sally(), out _)[2];
        Assert.Equal("altered", alt.Kind);
        Assert.Equal(["0", "1", "2", "3"], alt.Options.Select(o => o.Text));
        Assert.Equal("2", alt.ExpectedText);
        Assert.StartsWith("Sally has 3 brothers. Each of her brothers has 3", alt.Question);
    }

    [Fact]
    public void Same_option_text_has_the_same_id_in_every_variant()
    {
        var v = Resolve(Sally(), out _);
        Assert.Equal(v[1].Options.First(o => o.Text == "2").Id, v[2].Options.First(o => o.Text == "2").Id);
    }

    [Fact]
    public void Answer_must_be_one_of_the_options()
    {
        var q = Sally();
        q.VariantSpecs = [new VariantSpec { Id = "bad", Format = "choice", Answer = "7" }];
        Resolve(q, out var errors);
        Assert.Contains(errors, e => e.Contains("must be exactly one of the options"));
    }

    [Fact]
    public void Free_variant_of_a_choice_question_scores_the_base_answer()
    {
        var free = Resolve(Sally(), out _)[0];
        Assert.Equal(ItemStatus.Correct, Scorer.Score(free, [], "Final Answer: 1", "stop").Status);
        Assert.Equal(ItemStatus.Incorrect, Scorer.Score(free, [], "Final Answer: 2", "stop").Status);
    }

    [Fact]
    public void Loader_validates_a_catalog_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmbench-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "catalogs"));
        Directory.CreateDirectory(Path.Combine(dir, "prompts"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "catalogs", "t.json"), """
                [ { "id": "Q1", "tier": 1,
                    "base": { "question": "Which is a colour?", "options": ["red", "chair"], "answer": "red" },
                    "variants": [ { "id": "mcq", "format": "choice" },
                                  { "id": "alt", "kind": "altered", "format": "choice" } ] } ]
                """);
            var c = CatalogLoader.Load(new Workspace(dir), "catalogs/t.json");
            Assert.Contains(c.Errors, e => e.Contains("altered variant needs its own 'question'"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Control_pairs_use_original_and_altered()
    {
        ItemRecord Item(string v, string kind, bool ok) => new()
        {
            QuestionId = "CS-101", VariantId = v, VariantKind = kind, VariantType = "free",
            Status = ok ? ItemStatus.Correct : ItemStatus.Incorrect,
        };
        var items = new List<ItemRecord> { Item("free", "original", true), Item("altered-free", "altered", false), Item("mcq", "original", true) };
        var cp = SummaryBuilder.Build(items, items.Count).ControlPairs!;
        Assert.Equal(1, cp.Pairs);
        Assert.Equal(["CS-101/altered-free"], cp.RecognizesOnly);
    }
}
