using System.Globalization;
using LLMBench.Core;
using LLMBench.Core.Runs;

namespace LLMBench.Cli;

/// <summary>Applies command line options to a run definition.</summary>
public static class RunOptions
{
    public static void Apply(RunDefinition d, Args a)
    {
        foreach (var (key, value) in a.Options)
        {
            switch (key.ToLowerInvariant())
            {
                case "workspace" or "from": break;
                case "server": d.ServerUrl = Req(key, value); break;
                case "model": d.Model = value ?? ""; break;
                case "ctx": d.ExpectedContextSize = NullableInt(key, value); break;
                case "notes": d.ServerNotes = value ?? ""; break;
                case "description": d.Description = value; break;
                case "catalog": d.CatalogFile = Req(key, value); break;
                case "variants": d.Variants = List(value); if (d.Variants.Count == 0) d.Variants = ["all"]; break;
                case "categories": d.Categories = List(value); break;
                case "tiers": d.Tiers = List(value).Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToList(); break;
                case "ids": d.QuestionIds = List(value); break;
                case "limit": d.Limit = int.Parse(Req(key, value), CultureInfo.InvariantCulture); break;
                case "system-prompt":
                    d.SystemPromptFile = IsNone(value) ? null : Req(key, value);
                    break;
                case "placement": d.SystemPromptPlacement = Req(key, value).ToLowerInvariant(); break;
                case "instruction": d.AnswerInstruction = Req(key, value).ToLowerInvariant(); break;
                case "thinking": d.Thinking = Req(key, value).ToLowerInvariant(); break;
                case "thinking-on": d.ThinkingOn = Json.ParseObject(value); break;
                case "thinking-off": d.ThinkingOff = Json.ParseObject(value); break;
                case "temperature": d.Temperature = NullableDouble(key, value); break;
                case "top-p": d.TopP = NullableDouble(key, value); break;
                case "top-k": d.TopK = NullableInt(key, value); break;
                case "min-p": d.MinP = NullableDouble(key, value); break;
                case "repeat-penalty": d.RepeatPenalty = NullableDouble(key, value); break;
                case "presence-penalty": d.PresencePenalty = NullableDouble(key, value); break;
                case "seed": d.Seed = IsNone(value) ? null : long.Parse(Req(key, value), CultureInfo.InvariantCulture); break;
                case "max-tokens": d.MaxTokens = NullableInt(key, value); break;
                case "repetitions": d.Repetitions = int.Parse(Req(key, value), CultureInfo.InvariantCulture); break;
                case "shuffle": d.ShuffleOptions = true; break;
                case "no-shuffle": d.ShuffleOptions = false; break;
                case "shuffle-seed": d.ShuffleSeed = int.Parse(Req(key, value), CultureInfo.InvariantCulture); break;
                case "timeout": d.TimeoutSeconds = int.Parse(Req(key, value), CultureInfo.InvariantCulture); break;
                case "extra": d.ExtraRequest = Json.ParseObject(value); break;
                default: throw new UsageException($"Unknown option --{key}");
            }
        }
    }

    private static bool IsNone(string? v) => v is null || v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("default", StringComparison.OrdinalIgnoreCase) || v.Length == 0;

    private static string Req(string key, string? v) => v ?? throw new UsageException($"--{key} needs a value.");

    private static List<string> List(string? v) =>
        (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static double? NullableDouble(string key, string? v) =>
        IsNone(v) ? null : double.Parse(Req(key, v).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int? NullableInt(string key, string? v) =>
        IsNone(v) ? null : int.Parse(Req(key, v), CultureInfo.InvariantCulture);
}
