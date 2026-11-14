using System.Text.Json.Serialization;

namespace LLMBench.Core.Catalog;

/// <summary>
/// One question of a catalog: a <see cref="Base"/> with the shared data (main question, answer list, answer)
/// and the <see cref="VariantSpecs"/> that are actually asked. A variant only states what differs from the base.
/// </summary>
public sealed class CatalogQuestion
{
    public string Id { get; set; } = "";
    public string? Source { get; set; }
    public string? SourceId { get; set; }
    public string? Category { get; set; }
    /// <summary>Default tier (1-9) of all variants; a variant may override it.</summary>
    public int? Tier { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Note { get; set; }

    public QuestionBase Base { get; set; } = new();

    /// <summary>The variants as written in the catalog file.</summary>
    [JsonPropertyName("variants")]
    public List<VariantSpec> VariantSpecs { get; set; } = [];

    /// <summary>The variants resolved against the base (complete questions); filled by the loader.</summary>
    [JsonIgnore]
    public List<QuestionVariant> Variants { get; set; } = [];
}

/// <summary>Main question with everything the variants inherit.</summary>
public sealed class QuestionBase
{
    public string Question { get; set; } = "";
    /// <summary>"number" or "text" (default): how free answers are compared.</summary>
    public string? AnswerType { get; set; }
    /// <summary>Answer list for multiple choice (correct answer plus distractors, in a sensible order).</summary>
    public List<string> Options { get; set; } = [];
    /// <summary>The correct answer (for multiple choice: exactly one of <see cref="Options"/>).</summary>
    public string Answer { get; set; } = "";
    /// <summary>Further accepted free answers (besides <see cref="Answer"/>).</summary>
    public List<string> AcceptedAnswers { get; set; } = [];
    /// <summary>Never shuffle the options (e.g. ascending numbers, "cannot be determined" last).</summary>
    public bool FixedOrder { get; set; }
    public string? Image { get; set; }
    public string? Note { get; set; }
}

/// <summary>A concrete variant as written in the catalog: only the differences to the base.</summary>
public sealed class VariantSpec
{
    public string Id { get; set; } = "";
    /// <summary>"original" (default), "altered" (detail changed so the memorized answer is wrong), "distractor", "paraphrase", "perturbed".</summary>
    public string? Kind { get; set; }
    /// <summary>"choice" (with options) or "free" (no options). Default: choice if the base has options, else free.</summary>
    public string? Format { get; set; }
    public string? Question { get; set; }
    public List<string>? Options { get; set; }
    /// <summary>Number of options to show: the correct answer plus the first distractors of the list.</summary>
    public int? OptionCount { get; set; }
    public string? Answer { get; set; }
    public string? AnswerType { get; set; }
    public List<string>? AcceptedAnswers { get; set; }
    public bool? FixedOrder { get; set; }
    public int? Tier { get; set; }
    public string? Image { get; set; }
    public string? Note { get; set; }
}

/// <summary>A variant resolved against its base: a complete question as it is asked and scored.</summary>
public sealed class QuestionVariant
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "original";
    /// <summary>"choice" or "free".</summary>
    public string Type { get; set; } = "choice";
    public string? AnswerType { get; set; }
    public string Question { get; set; } = "";
    public string? Image { get; set; }
    public List<AnswerOption> Options { get; set; } = [];
    public string? CorrectOptionId { get; set; }
    public List<string> AcceptedAnswers { get; set; } = [];
    public bool FixedOrder { get; set; }
    public int? Tier { get; set; }
    public string? Note { get; set; }

    [JsonIgnore] public bool IsChoice => string.Equals(Type, "choice", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsNumber => string.Equals(AnswerType, "number", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string ExpectedText => IsChoice
        ? Options.FirstOrDefault(o => o.Id == CorrectOptionId)?.Text ?? $"?{CorrectOptionId}"
        : string.Join(" | ", AcceptedAnswers);
}

public sealed class AnswerOption
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>A loaded and validated catalog file.</summary>
public sealed class QuestionCatalog
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string Sha256 { get; init; }
    public required List<CatalogQuestion> Questions { get; init; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsValid => Errors.Count == 0;

    public int VariantCount => Questions.Sum(q => q.Variants.Count);

    public IReadOnlyList<string> VariantIds =>
        Questions.SelectMany(q => q.Variants).Select(v => v.Id).Distinct().ToList();

    public IReadOnlyList<string> Categories =>
        Questions.Select(q => q.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList()!;
}
