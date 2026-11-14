using System.Globalization;
using LLMBench.Core.Runs;

namespace LLMBench.Core.Stats;

/// <summary>
/// Time and token figures of a run. Tokens per answer do not depend on the hardware; seconds and tokens per second
/// are only comparable between runs on the same machine with the same server settings.
/// </summary>
public static class RunTiming
{
    public static double? SecondsPerAnswer(RunSummary s) => s.Done > 0 ? s.RequestSeconds / s.Done : null;

    public static double? TokensPerAnswer(RunSummary s) => s.Done > 0 ? (double)s.CompletionTokens / s.Done : null;

    public static double? TokensPerSecond(RunSummary s) => s.RequestSeconds > 0 ? s.CompletionTokens / s.RequestSeconds : null;

    /// <summary>"45 s", "12.5 min", "2 h 05 min".</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 60) return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
        if (seconds < 3600) return (seconds / 60).ToString("0.0", CultureInfo.InvariantCulture) + " min";
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours} h {t.Minutes:00} min";
    }

    /// <summary>One line: model time, seconds per answer, tokens per answer, tokens per second.</summary>
    public static string Line(RunSummary s)
    {
        var inv = CultureInfo.InvariantCulture;
        return $"model time {Duration(s.RequestSeconds)} | {SecondsPerAnswer(s)?.ToString("0.0", inv) ?? "-"} s/answer | " +
               $"{TokensPerAnswer(s)?.ToString("0", inv) ?? "-"} tokens/answer | {TokensPerSecond(s)?.ToString("0.0", inv) ?? "-"} tokens/s";
    }
}
