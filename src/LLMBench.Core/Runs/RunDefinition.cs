using System.Text.Json.Nodes;

namespace LLMBench.Core.Runs;

/// <summary>
/// All parameters of a named run (concept section 9). Saved as runs/&lt;name&gt;/run.json.
/// Nullable sampling values mean "not sent" (the server default applies; actual values are in the run header via /props).
/// </summary>
public sealed class RunDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? CreatedAt { get; set; }
    /// <summary>Set when this run was created by "restart" from another run.</summary>
    public string? RestartOf { get; set; }

    // Server / model
    public string ServerUrl { get; set; } = "http://127.0.0.1:8081";
    /// <summary>Model name; empty = taken from the server alias when the run starts.</summary>
    public string Model { get; set; } = "";
    /// <summary>If set, preflight aborts when the server's n_ctx differs (context size is a server start parameter).</summary>
    public int? ExpectedContextSize { get; set; }
    /// <summary>Free text: server start command, hardware, notes. Stored in the run header.</summary>
    public string ServerNotes { get; set; } = "";

    // Questions
    public string CatalogFile { get; set; } = "catalogs/core-v5.json";
    /// <summary>Variant ids to run, or ["all"].</summary>
    public List<string> Variants { get; set; } = ["all"];
    public List<string> Categories { get; set; } = [];
    public List<int> Tiers { get; set; } = [];
    public List<string> QuestionIds { get; set; } = [];
    /// <summary>Max number of questions (0 = all).</summary>
    public int Limit { get; set; }

    // Prompt
    /// <summary>Path relative to the workspace, or null for no system prompt.</summary>
    public string? SystemPromptFile { get; set; } = "prompts/neutral.txt";
    /// <summary>"system" (own role) or "user" (prepended to the user message).</summary>
    public string SystemPromptPlacement { get; set; } = "system";
    public string AnswerInstruction { get; set; } = "none";

    // Repetition / shuffling
    public int Repetitions { get; set; } = 1;
    public bool ShuffleOptions { get; set; } = true;
    public int ShuffleSeed { get; set; } = 1;

    // Thinking
    /// <summary>"on", "off" or "default" (nothing sent).</summary>
    public string Thinking { get; set; } = "off";
    public JsonObject ThinkingOn { get; set; } = new() { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true } };
    public JsonObject ThinkingOff { get; set; } = new() { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } };

    // Sampling (null = not sent)
    public double? Temperature { get; set; } = 0.0;
    public double? TopP { get; set; } = 0.95;
    public int? TopK { get; set; }
    public double? MinP { get; set; }
    public double? RepeatPenalty { get; set; } = 1.0;
    public double? PresencePenalty { get; set; } = 0.0;
    public long? Seed { get; set; } = 42;
    public int? MaxTokens { get; set; } = 7000;

    public int TimeoutSeconds { get; set; } = 600;
    /// <summary>Merged into the request last (top-level keys overwrite).</summary>
    public JsonObject ExtraRequest { get; set; } = new() { ["cache_prompt"] = false };

    public RunDefinition Clone() => Json.Deserialize<RunDefinition>(Json.Serialize(this))!;

    /// <summary>Copy without the run identity (name, dates, description), e.g. for the defaults file.</summary>
    public RunDefinition AsTemplate()
    {
        var t = Clone();
        t.Name = "";
        t.Description = null;
        t.CreatedAt = null;
        t.RestartOf = null;
        return t;
    }

    public bool AllVariants => Variants.Count == 0 || Variants.Any(v => string.Equals(v, "all", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a list of problems with the parameters (empty = ok).</summary>
    public List<string> Check()
    {
        var p = new List<string>();
        if (!RunStore.IsValidName(Name)) p.Add("Name may only contain letters, digits, '.', '-' and '_' (max 80 characters).");
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out _)) p.Add("Server URL is not a valid URL.");
        if (string.IsNullOrWhiteSpace(CatalogFile)) p.Add("No catalog selected.");
        if (Thinking is not ("on" or "off" or "default")) p.Add("Thinking must be on, off or default.");
        if (SystemPromptPlacement is not ("system" or "user")) p.Add("System prompt placement must be system or user.");
        if (!Prompting.AnswerInstructions.All.Contains(AnswerInstruction)) p.Add("Answer instruction must be reasoned, direct or none.");
        if (Repetitions < 1) p.Add("Repetitions must be at least 1.");
        if (TimeoutSeconds < 1) p.Add("Timeout must be at least 1 second.");
        if (MaxTokens is <= 0) p.Add("Max tokens must be positive (or empty for server default).");
        return p;
    }
}
