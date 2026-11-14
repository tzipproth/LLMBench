using System.Text.Json.Nodes;
using LLMBench.Core.Prompting;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Runs;

/// <summary>First line of results.jsonl.</summary>
public sealed class RunHeader
{
    public string Type { get; set; } = "run";
    public string RunName { get; set; } = "";
    public string RunId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string LlmBenchVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public RunDefinition Definition { get; set; } = new();
    public string? SystemPromptFile { get; set; }
    public string? SystemPrompt { get; set; }
    public string? SystemPromptSha256 { get; set; }
    public string CatalogFile { get; set; } = "";
    public string CatalogSha256 { get; set; } = "";
    public int CatalogQuestions { get; set; }
    public int PlannedItems { get; set; }
    /// <summary>Content hash of the planned (question, variant) pairs; equal hash = same questions (leaderboard group).</summary>
    public string? QuestionSetHash { get; set; }
    /// <summary>Number of distinct (question, variant) pairs.</summary>
    public int QuestionSetCount { get; set; }
    public List<SkippedEntry> SkippedVariants { get; set; } = [];
    public string? ServerHealth { get; set; }
    public string? ModelAlias { get; set; }
    public string? ModelPath { get; set; }
    public int? ContextSize { get; set; }
    public bool? VisionAvailable { get; set; }
    /// <summary>Complete response of GET /props (build, template, default sampler settings, modalities ...).</summary>
    public JsonNode? ServerProps { get; set; }
    public WarmupInfo? Warmup { get; set; }
    /// <summary>Result of the thinking probe; null when thinking = default (nothing to verify).</summary>
    public bool? ThinkingVerified { get; set; }
    public LLMBench.Core.Llama.ThinkingProbeResult? ThinkingProbe { get; set; }
    public List<string> Warnings { get; set; } = [];
    /// <summary>Log of rescoring (stored replies scored again with newer scoring rules).</summary>
    public List<string> Rescored { get; set; } = [];
    /// <summary>Set by "merge": the runs whose results this run combines (no requests of its own).</summary>
    public List<string> MergedFrom { get; set; } = [];
}

public sealed class WarmupInfo
{
    public bool Ok { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public string? FinishReason { get; set; }
    public string? Error { get; set; }
}

/// <summary>One line per request in results.jsonl.</summary>
public sealed class ItemRecord
{
    public string Type { get; set; } = "item";
    public int Index { get; set; }
    public string QuestionId { get; set; } = "";
    public string VariantId { get; set; } = "";
    public string VariantType { get; set; } = "";
    /// <summary>original, altered, distractor, ... (null in result files written before the base/variant format).</summary>
    public string? VariantKind { get; set; }
    public string? AnswerType { get; set; }
    public string? Category { get; set; }
    public int? Tier { get; set; }
    public List<string> Tags { get; set; } = [];
    public int Repetition { get; set; }
    public long? Seed { get; set; }
    public List<DisplayedOption> DisplayedOptions { get; set; } = [];
    public string? Image { get; set; }
    public string? SystemMessage { get; set; }
    public string UserMessage { get; set; } = "";
    public string Expected { get; set; } = "";
    public string? CorrectOptionId { get; set; }
    public string? CorrectLetter { get; set; }
    public List<string>? AcceptedAnswers { get; set; }
    public string? ExtractedAnswer { get; set; }
    public string? ChosenLetter { get; set; }
    public string? ChosenOptionId { get; set; }
    public ItemStatus Status { get; set; }
    public string? Reason { get; set; }
    public bool ThinkStripped { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public string? FinishReason { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public JsonNode? Usage { get; set; }
    public JsonNode? Timings { get; set; }
    public double ElapsedSeconds { get; set; }
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; }
    public JsonNode? Request { get; set; }

    public string Key => $"{QuestionId}/{VariantId}";
    public int OptionCount => DisplayedOptions.Count;
}

public sealed class GroupStat
{
    public string Key { get; set; } = "";
    public int N { get; set; }
    public int Correct { get; set; }
    public double? Accuracy => N > 0 ? (double)Correct / N : null;
}

public sealed class StabilityStat
{
    public int QuestionVariants { get; set; }
    public int SolvedInAll { get; set; }
    public int SolvedInNone { get; set; }
    public double MeanSolveRate { get; set; }
}

/// <summary>
/// Practical view: how good the model is when the user does not want to wait long. Computed from the stored answers,
/// no new run needed. At temperature 0 a run with a smaller max_tokens produces exactly the beginning of the longer
/// answer, so "correct with at most N output tokens" is what that run would score (thinking tokens included).
/// Token figures do not depend on the hardware; the seconds figures only hold for this machine and server settings.
/// </summary>
public sealed class BudgetStat
{
    public static readonly int[] TokenLimits = [1000, 2000, 4000];
    public static readonly int[] SecondLimits = [30, 60];

    public double MedianTokens { get; set; }
    public double MedianSeconds { get; set; }
    /// <summary>All output tokens of the scored answers divided by the correct ones (efficiency; null without a correct answer).
    /// Only meaningful between models of similar accuracy - a weak model can be cheap per hit by being short.</summary>
    public double? TokensPerCorrect { get; set; }
    /// <summary>
    /// Net value per scored answer: correct +1, incorrect -1 for free answers and -1/(k-1) for a choice question with
    /// k options (so pure guessing is worth 0 on average), invalid / truncated 0. Range -1 .. 1.
    /// </summary>
    public double NetScore { get; set; }
    /// <summary>Net value (sum, not share) per minute of request time on this machine.</summary>
    public double? NetPerMinute { get; set; }
    /// <summary>Accuracy if every answer had to fit into TokenLimits[i] output tokens.</summary>
    public List<double> AccuracyAtTokens { get; set; } = [];
    /// <summary>Accuracy if every answer had to arrive within SecondLimits[i] seconds.</summary>
    public List<double> AccuracyWithinSeconds { get; set; } = [];
}

/// <summary>
/// Concept 5.7: a trap variant paired with the "control" variant of the same question and repetition.
/// </summary>
public sealed class ControlPairStat
{
    public int Pairs { get; set; }
    /// <summary>trap right, control right: reads and understands.</summary>
    public List<string> BothCorrect { get; set; } = [];
    /// <summary>trap wrong, control right: only recognizes the known puzzle.</summary>
    public List<string> RecognizesOnly { get; set; } = [];
    /// <summary>trap right, control wrong: over-suspicious.</summary>
    public List<string> OverSuspicious { get; set; } = [];
    /// <summary>both wrong: task too hard.</summary>
    public List<string> BothWrong { get; set; } = [];
}

/// <summary>Last line of results.jsonl.</summary>
public sealed class RunSummary
{
    public string Type { get; set; } = "summary";
    public bool Completed { get; set; }
    public bool Aborted { get; set; }
    public string? AbortReason { get; set; }
    public DateTime FinishedAt { get; set; }
    public double DurationSeconds { get; set; }
    public int Planned { get; set; }
    public int Done { get; set; }
    public int Correct { get; set; }
    public int Incorrect { get; set; }
    public int Invalid { get; set; }
    public int Truncated { get; set; }
    public int Error { get; set; }
    public int Skipped { get; set; }
    /// <summary>Denominator: correct + incorrect + invalid + truncated.</summary>
    public int Scored { get; set; }
    public double? Accuracy { get; set; }
    public double? CiLow { get; set; }
    public double? CiHigh { get; set; }
    /// <summary>Number of distinct question/variant pairs the CI is based on (repetitions do not add questions).</summary>
    public int CiQuestions { get; set; }
    /// <summary>Mean of 1/option count over scored choice items.</summary>
    public double? ChanceLevel { get; set; }
    public int ChoiceItems { get; set; }
    public int FreeItems { get; set; }
    public Dictionary<string, int> InvalidReasons { get; set; } = [];
    public List<GroupStat> ByCategory { get; set; } = [];
    public List<GroupStat> ByVariant { get; set; } = [];
    public List<GroupStat> ByTier { get; set; } = [];
    public StabilityStat? Stability { get; set; }
    public ControlPairStat? ControlPairs { get; set; }
    public BudgetStat? Budget { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public double RequestSeconds { get; set; }
}
