using System.Globalization;
using System.Net;
using System.Text;
using LLMBench.Core.Runs;
using LLMBench.Core.Stats;

namespace LLMBench.Heatmap;

/// <summary>
/// The public leaderboard: one self-contained HTML page with the key columns, a score chart inside the table and
/// plain explanations of every column and of the method. No external files, fonts or libraries.
/// </summary>
public static class LeaderboardHtml
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(LeaderboardGroup group, IList<LeaderboardEntry> shown, string heatmapFile, string gpu, int vramGb, string system,
                                IReadOnlyDictionary<string, ModelInfo> info)
    {
        var rows = Leaderboard.AssignRanks(shown.ToList());
        var first = rows[0];
        var def = first.Definition;
        var cal = group.Calibration;
        var reference = cal?.ReferenceRun is { } rr ? rows.FirstOrDefault(e => e.RunName == rr)?.Model ?? rr : null;
        bool hasStrength = reference != null && rows.Any(e => e.Ability?.Strength != null);

        // common log scale of the strength chart: 1-2-5 steps around all 95 % ranges
        double[] ticks = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
        double lo = 0.1, hi = 100;
        if (hasStrength)
        {
            var withS = rows.Where(e => e.Ability?.Strength != null).ToList();
            double min = withS.Min(e => e.Ability!.StrengthLow!.Value), max = withS.Max(e => e.Ability!.StrengthHigh!.Value);
            lo = ticks.LastOrDefault(t => t <= min, ticks[0]);
            hi = ticks.FirstOrDefault(t => t >= max, ticks[^1]);
        }
        double Frac(double v) => 100 * (Math.Log(v) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo));
        string Pos(double v) => Frac(v).ToString("0.##", Inv) + "%";

        var sb = new StringBuilder();
        sb.Append(Head);
        sb.Append($"<main>\n<header class=\"top\">\n<p class=\"eyebrow\">LLMBench</p>\n<h1>LLM general intelligence leaderboard for {vramGb} GB graphics cards</h1>\n");
        bool anyRam = rows.Any(e => e.ExpertsInRam);
        bool anyNativeThinking = rows.Any(e => e.UsesNativeThinking);
        sb.Append($"<p class=\"lede\">Which local LLM thinks best on a graphics card with {vramGb} GB? " +
                  (anyRam
                      ? $"Every dense model here fits completely into {vramGb} GB of VRAM; MoE models that are too large can keep part of their experts in system RAM without losing too much speed (marked <span class=\"badge\">MoE · RAM</span>). "
                      : $"Every model here fits completely into {vramGb} GB of VRAM. ") +
                  $"All {rows.Count} answer the same {group.QuestionCount} reasoning questions on the same {H(gpu)}. " +
                  "Spatial and physical reasoning, keeping track of objects, altered classic puzzles, logic, math, time and social reasoning: " +
                  "tasks where a model has to think, not recall.</p>\n");
        sb.Append("<dl class=\"facts\">");
        Fact(sb, first.Header.CatalogQuestions.ToString(Inv), "questions");
        Fact(sb, group.QuestionCount.ToString(Inv), "question variants");
        Fact(sb, rows.Count.ToString(Inv), "models");
        Fact(sb, gpu, $"GPU, {vramGb} GB VRAM");
        sb.Append("</dl>\n</header>\n");

        // ---- table -------------------------------------------------------------------------------------
        sb.Append("<section class=\"board\">\n<p class=\"note scroll-hint\" id=\"lb-overflow\" hidden>Scroll right to see the remaining columns.</p>\n" +
                  "<div class=\"scroll\" tabindex=\"0\" role=\"region\" aria-label=\"Model results\"><table id=\"lb\">\n<thead><tr>");
        Th(sb, "#", "num", "pos", "Position in the list");
        Th(sb, "Rank", "num", "rank", "Shared rank = not distinguishable (see below)");
        Th(sb, "Model", "", "model", null);
        if (hasStrength)
        {
            Th(sb, "Strength", "num", "strength", $"Odds of solving a question, compared with {reference} (= 1×); 95 % range below");
            sb.Append("<th class=\"plot\" aria-hidden=\"true\"><div class=\"axis\">");
            foreach (var t in ticks.Where(t => t >= lo && t <= hi))
                sb.Append($"<span style=\"left:{Pos(t)}\">{t.ToString("0.##", Inv)}×</span>");
            sb.Append("</div></th>");
        }
        Th(sb, "Quality per time", "num", "npm", $"Right minus wrong answers per minute on the {gpu}");
        Th(sb, "Accuracy", "num", "acc", "Share of correct answers, 95 % range");
        Th(sb, "Correct", "num", "correct", "Correct answers / scored answers");
        Th(sb, "s / answer", "num", "spa", $"Mean seconds per answer on the {gpu}");
        Th(sb, "Quality", "num", "net", "Right minus wrong answers, per 100 answers");
        Th(sb, "Invalid answers", "num", "invalid", "Share of scored answers with no usable final answer");
        Th(sb, "Token limit reached", "num", "truncated", "Share of scored answers cut off at the output token limit");
        sb.Append("</tr></thead>\n<tbody>\n");

        foreach (var e in rows)
        {
            var s = e.Summary;
            var b = s.Budget;
            var a = e.Ability;
            bool flagged = e.UsabilityNote.Length > 0;
            sb.Append($"<tr{(e.Rank == 1 ? " class=\"top1\"" : "")}>");
            Td(sb, e.Position.ToString(Inv), e.Position, "num muted");
            Td(sb, $"<span class=\"rank\">{e.Rank?.ToString(Inv) ?? "-"}</span>", e.Rank ?? 999, "num");
            // only ".gguf" is cut: an EXL3 model is a folder whose name has dots ("Qwen3.8-27B-exl3-4bpw")
            var file = e.ModelFile.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? e.ModelFile[..^5] : e.ModelFile;
            var thinkingBadge = e.UsesNativeThinking
                ? " <span class=\"badge\" title=\"Uses native-thinking.txt with thinking enabled or left to the server\">Native thinking</span>"
                : "";
            Td(sb, $"<span class=\"name\">{H(e.Model)}{(flagged ? "<sup class=\"flag\" title=\"" + H(e.UsabilityNote) + "\">†</sup>" : "")}" +
                   $"{(e.ExpertsInRam ? $" <span class=\"badge\" title=\"MoE model: {(e.RamSharePercent is { } rp ? rp + " % of the weights" : "part of the experts")} in system RAM ({H(system)}), the rest in VRAM\">MoE · {(e.RamSharePercent is { } rs ? rs + " % " : "")}RAM</span>" : "")}{thinkingBadge}</span>" +
                   $"<span class=\"sub\">{H(file)}</span>", e.Model, "model");
            if (hasStrength)
            {
                if (a?.Strength is { } st)
                {
                    Td(sb, $"{Rasch.FormatStrength(st)}×<span class=\"sub\">{Rasch.FormatStrength(a.StrengthLow)}–{Rasch.FormatStrength(a.StrengthHigh)}</span>", st, "num strong");
                    sb.Append($"<td class=\"plot\"><div class=\"track\" title=\"Strength {Rasch.FormatStrength(st)}×, 95 % range {Rasch.FormatStrength(a.StrengthLow)}–{Rasch.FormatStrength(a.StrengthHigh)}\">" +
                              $"<span class=\"ref\" style=\"left:{Pos(1)}\"></span>" +
                              $"<span class=\"ci\" style=\"left:{Pos(a.StrengthLow!.Value)};right:{(100 - Frac(a.StrengthHigh!.Value)).ToString("0.##", Inv)}%\"></span>" +
                              $"<span class=\"dot\" style=\"left:{Pos(st)}\"></span></div></td>");
                }
                else { Td(sb, "-", -1.0, "num"); sb.Append("<td class=\"plot\"></td>"); }
            }
            Td(sb, b?.NetPerMinute is { } npm ? npm.ToString("0.0", Inv).Replace("-", "−") + "<span class=\"pm\">/ min</span>" : "-", b?.NetPerMinute ?? -9, "num strong");
            Td(sb, s.Accuracy is { } acc ? $"{Pct(acc)}<span class=\"sub\">{(s.CiLow ?? 0) * 100:0}–{(s.CiHigh ?? 0) * 100:0} %</span>" : "-", s.Accuracy ?? -1, "num");
            Td(sb, $"{s.Correct}<span class=\"muted\"> / {s.Scored}</span>", s.Correct, "num");
            Td(sb, e.SecondsPerAnswer is { } spa ? spa.ToString("0.0", Inv) : "-", e.SecondsPerAnswer ?? 1e9, "num");
            Td(sb, b != null ? Pct(b.NetScore) : "-", b?.NetScore ?? -9, "num");
            TdAnswerShare(sb, s.Invalid, s.Scored);
            TdAnswerShare(sb, s.Truncated, s.Scored);
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table></div>\n");
        if (rows.Any(e => e.UsabilityNote.Length > 0))
            sb.Append("<p class=\"note\"><span class=\"flag\">†</span> Run warning. A combined share of 5 % or more truncated or invalid answers triggers this flag. " +
                      "Hover over the symbol for the reason.</p>\n");
        if (anyRam)
            sb.Append($"<p class=\"note\"><span class=\"badge\">MoE · xx % RAM</span> Mixture-of-experts model too large for {vramGb} GB: xx % of its weights " +
                      $"(experts that do not fit) stay in system RAM ({H(system)}), everything else in VRAM. Why this is allowed: see “MoE · RAM” below.</p>\n");
        sb.Append("<p class=\"note\">Click a column header to sort. Rows with rank 1 are highlighted.</p>\n</section>\n");

        // ---- explanations ------------------------------------------------------------------------------
        sb.Append("<section class=\"explain\">\n<h2>How to read the columns</h2>\n<dl class=\"cols\">\n");
        Col(sb, "Rank", "1 + the number of models that are <em>significantly</em> better. Two models share a rank when the difference between them " +
                        "could be chance: every pair of models is compared question by question (paired McNemar test, p &lt; 0.05).");
        Col(sb, "Model", "The model name and below it the exact model file (quantization), so the result can be reproduced.");
        if (anyNativeThinking)
            Col(sb, "Native thinking", "The system prompt asks for an accurate final answer. The model's chat template handles the " +
                                       "thinking instructions and any effort setting. The badge identifies the run configuration.");
        if (anyRam)
            Col(sb, "MoE · RAM", $"A mixture-of-experts (MoE) model consists of many small expert networks, but uses only a few of them for each token " +
                                 $"(e.g. 3 of 35 billion parameters). Dense models have to fit completely into the {vramGb} GB of VRAM. A MoE model that is too large " +
                                 $"may keep the experts that do not fit in system RAM, where the CPU computes them: since only a few experts are needed per token, " +
                                 $"it stays fast, and this is how many people with a {vramGb} GB card run these models. The percentage is the share of the weights " +
                                 $"in RAM; the speed of these models also depends on RAM and CPU ({H(system)}). Quality per time shows the cost of it.");
        if (hasStrength)
        {
            double top1 = rows[0].Ability?.Strength ?? 1;
            string P(double odds) => (100 * odds / (odds + 1)).ToString("0", Inv) + " %";
            Col(sb, "Strength", $"How much better a model is, as a plain factor: how many times higher its odds (right : wrong) of solving a question are than those of " +
                                $"<strong>{H(reference!)}</strong> (= 1×). Example: on a question that {H(reference!)} gets right half of the time (odds 1 : 1), " +
                                $"{H(rows[0].Model)} with {Rasch.FormatStrength(top1)}× has odds of {Rasch.FormatStrength(top1)} : 1 and is right {P(top1)} of the time; " +
                                $"on a question {H(reference!)} gets right only 1 time in 10 (odds 1 : 9), it is right {P(top1 / 9)} of the time. " +
                                "Ratios between models are meaningful too: 40× has twice the odds of 20×. " +
                                "The chart uses a log scale; the line shows the 95 % range. Where two lines overlap a lot, the order of the two models is not certain.");
        }
        Col(sb, "Quality per time", $"How much good answering a model delivers per minute: right minus wrong answers per minute of model time on the {H(gpu)}. " +
                                    "A model that is a little weaker but much faster can be the better choice in daily use; this column shows it. " +
                                    "Models in VRAM get faster or slower by a similar factor on another graphics card" +
                                    (anyRam ? "; for the models marked MoE · RAM, system RAM and CPU matter too." : "."));
        Col(sb, "Accuracy", "The share of correct answers. Below: the 95 % range (Wilson interval). It puts the models in the same order as the strength, " +
                            "but squeezes the top: near 100 % every further percent is much harder to gain than in the middle, the strength shows that.");
        Col(sb, "Correct", "Correct answers out of all scored answers.");
        Col(sb, "s / answer", $"Mean time per answer in seconds on the {H(gpu)}. Depends on the speed of the model and on how much it writes before it answers.");
        Col(sb, "Quality", "Right minus wrong answers per 100 answers: what the answers are worth if you trust them. No usable answer counts 0. " +
                           "A wrong multiple-choice answer with <em>k</em> options costs only 1/(<em>k</em>−1), so guessing gains nothing. " +
                           "A model that is often wrong drops further here than in accuracy. Quality per time is the same value per minute instead of per answer.");
        Col(sb, "Invalid answers", "Share of scored answers with no usable final answer. These count as wrong in accuracy.");
        Col(sb, "Token limit reached", "Share of scored answers cut off at the output token limit. These count as wrong in accuracy.");
        sb.Append("</dl>\n");
        Makers(sb, rows, info);

        sb.Append($"<a class=\"callout\" href=\"{H(heatmapFile)}\"><span class=\"callout-title\">Every single answer of every model</span>" +
                  "<span class=\"callout-text\">Heatmap of all results: one square per question and model, with the question, the answer and the time on hover.</span>" +
                  "<span class=\"callout-arrow\" aria-hidden=\"true\">→</span></a>\n");
        sb.Append("<h2>How the benchmark works</h2>\n<div class=\"method\">\n");
        sb.Append($"<p><strong>Questions.</strong> {first.Header.CatalogQuestions} questions, many of them in two forms (free answer and multiple choice), " +
                  $"{group.QuestionCount} variants in total. Some are well-known puzzles with a twist that changes the answer, so a memorized solution is wrong. " +
                  "The options of multiple-choice questions are shuffled the same way for every model.</p>\n");
        sb.Append($"<p><strong>Hardware and settings.</strong> Each model runs locally on one {H(gpu)} with {vramGb} GB of VRAM using " +
                  "llama-server (llama.cpp) or TabbyAPI / ExLlamaV3 for EXL3 quantizations. " +
                  (anyRam ? $"MoE models can keep part of their experts in system RAM ({H(system)}). " : "") +
                  "Every question is sent on its own. ");
        if (rows.All(e => e.Definition.Temperature == def.Temperature && e.Definition.Seed == def.Seed && e.Definition.MaxTokens == def.MaxTokens))
        {
            string limit = def.MaxTokens is { } max ? $"an output limit of {max.ToString("#,0", Inv)} tokens" : "unlimited output";
            sb.Append($"These runs share temperature {N(def.Temperature)}, seed {N(def.Seed)} and {limit}.");
        }
        else sb.Append("Sampling settings and output limits are recorded for each run in the repository.");
        sb.Append("</p>\n");
        if (anyNativeThinking)
            sb.Append("<p><strong>Thinking.</strong> Runs marked Native thinking use the model's chat template for reasoning with the " +
                      "native-thinking system prompt. The original v5 setup uses neutral with thinking off where supported or left to the server.</p>\n");
        foreach (var prompt in rows.Where(e => e.Header.SystemPrompt is not null).GroupBy(e => e.Header.SystemPrompt!).OrderByDescending(g => g.Count()))
            sb.Append($"<p><strong>System prompt</strong> <code>{H(prompt.First().SystemPrompt)}</code></p>\n<blockquote>{H(prompt.Key)}</blockquote>\n");
        sb.Append("<p><strong>Scoring.</strong> Only the final answer counts: the line starting with “Final Answer:”, compared with the solution " +
                  "(numbers as numbers, options by letter or text). The reasoning before it is not graded. Answers cut off at the output limit count as wrong.</p>\n");
        if (cal != null)
            sb.Append($"<p><strong>Strength scale.</strong> The difficulty of every question was estimated once from the answers of the first {cal.Runs.Count} models (Rasch model with a guessing floor for " +
                      $"multiple choice) on {cal.CreatedAt:yyyy-MM-dd} and is now fixed. Models added later are placed on the same scale without changing it, and {H(reference ?? "the reference model")} stays the reference (= 1×).</p>\n");
        sb.Append("</div>\n</section>\n");

        sb.Append($"<footer>Generated {DateTime.Now:yyyy-MM-dd} by LLMBench {H(BenchRunner.Version)} · {H(string.Join(", ", group.CatalogFiles))} · question set {H(group.QuestionSetHash[..8])}</footer>\n");
        sb.Append("</main>\n");
        sb.Append(Script);
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);
    private static string Pct(double v) => (v * 100).ToString("0.0", Inv).Replace("-", "−") + " %";
    private static string N(double? v) => v?.ToString(Inv) ?? "default";
    private static string N(long? v) => v?.ToString(Inv) ?? "random";

    private static void Fact(StringBuilder sb, string value, string label) =>
        sb.Append($"<div><dt>{H(label)}</dt><dd>{H(value)}</dd></div>");

    private static void Th(StringBuilder sb, string text, string cls, string key, string? title) =>
        sb.Append($"<th class=\"{cls}\" data-key=\"{key}\"{(title != null ? $" title=\"{H(title)}\"" : "")} tabindex=\"0\" aria-sort=\"none\">{H(text)}</th>");

    private static void Td(StringBuilder sb, string html, object sortValue, string cls)
    {
        var v = sortValue switch
        {
            double d => d.ToString("R", Inv),
            int i => i.ToString(Inv),
            string s => H(s.ToLowerInvariant()),
            _ => "",
        };
        sb.Append($"<td class=\"{cls}\" data-v=\"{v}\">{html}</td>");
    }

    private static void TdAnswerShare(StringBuilder sb, int count, int scored)
    {
        if (scored == 0)
        {
            Td(sb, "-", -1, "num");
            return;
        }

        var share = (double)count / scored;
        Td(sb, $"{Pct(share)}<span class=\"sub\">{count} / {scored}</span>", share, "num");
    }

    /// <summary>
    /// Who made the models: one row per maker, grouped by country. The country with the strongest model comes first, within a
    /// country the maker with the strongest model; the models of a maker are listed from strong to weak. Models without an
    /// entry in model-info.json are left out.
    /// </summary>
    private static void Makers(StringBuilder sb, IList<LeaderboardEntry> rows, IReadOnlyDictionary<string, ModelInfo> info)
    {
        static double Key(LeaderboardEntry e) => e.Ability?.Strength ?? e.Summary.Accuracy ?? 0;
        var known = rows.Where(e => info.ContainsKey(e.Model) && info[e.Model].Maker.Length > 0).ToList();
        if (known.Count == 0) return;
        var makers = known
            .GroupBy(e => (info[e.Model].Maker, info[e.Model].Country))
            .Select(g => (g.Key.Maker, g.Key.Country, Models: g.OrderByDescending(Key).ToList()))
            .GroupBy(m => m.Country)
            .OrderByDescending(c => c.Max(m => Key(m.Models[0])))
            .SelectMany(c => c.OrderByDescending(m => Key(m.Models[0])))
            .ToList();

        sb.Append("<h2>Who made the models</h2>\n<div class=\"scroll\"><table class=\"makers\">\n<thead><tr><th>Company</th><th>Country</th>" +
                  "<th>Models, strongest first <span class=\"muted\">(strength · quantized by)</span></th></tr></thead>\n<tbody>\n");
        string? lastCountry = null;
        foreach (var (maker, country, models) in makers)
        {
            bool first = country != lastCountry;
            lastCountry = country;
            sb.Append($"<tr{(first ? " class=\"country-start\"" : "")}><td class=\"name\">{H(maker)}</td><td>{(first ? H(country) : "")}</td><td>");
            foreach (var e in models)
            {
                var gguf = info[e.Model].Gguf;
                var strength = e.Ability?.Strength is { } st ? $"{Rasch.FormatStrength(st)}×" : "";
                sb.Append($"<span class=\"chip\">{H(e.Model)} <b>{strength}</b>{(gguf.Length > 0 ? $" <span class=\"muted\">· {H(gguf)}</span>" : "")}</span>");
            }
            sb.Append("</td></tr>\n");
        }
        sb.Append("</tbody></table></div>\n");
    }

    private static void Col(StringBuilder sb, string name, string html) =>
        sb.Append($"<div><dt>{H(name)}</dt><dd>{html}</dd></div>\n");

    private const string Head = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>LLMBench Leaderboard</title>
<meta name="description" content="Reasoning leaderboard for local open-weight LLMs that run on a 16 GB graphics card.">
<style>
:root {
  color-scheme: light;
  --bg: #f6f6f4; --surface: #fcfcfb; --line: #e4e3df; --line-strong: #cfcdc7;
  --text: #0b0b0b; --text-2: #52514e; --text-3: #7a7974;
  --accent: #2a78d6; --accent-soft: #2a78d61f; --top: #2a78d60f;
  --track: #ecebe7; --flag: #b2560f; --green: #1e8c32;
}
@media (prefers-color-scheme: dark) {
  :root {
    color-scheme: dark;
    --bg: #121211; --surface: #1a1a19; --line: #2c2c2a; --line-strong: #3d3d3a;
    --text: #f0efec; --text-2: #c3c2b7; --text-3: #8f8e86;
    --accent: #3987e5; --accent-soft: #3987e52e; --top: #3987e514;
    --track: #262624; --flag: #e8914a; --green: #3fb450;
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text);
  font: 15px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
main { max-width: 1240px; margin: 0 auto; padding: 48px 16px 40px; }
a { color: var(--accent); }
.eyebrow { margin: 0 0 6px; font-size: 13px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; color: var(--accent); }
h1 { margin: 0 0 12px; font-size: clamp(26px, 4vw, 38px); line-height: 1.15; letter-spacing: -.01em; }
h2 { margin: 48px 0 16px; font-size: 21px; }
.lede { margin: 0; max-width: 760px; font-size: 17px; color: var(--text-2); }
.facts { display: flex; flex-wrap: wrap; gap: 12px; margin: 28px 0 0; padding: 0; }
.facts div { background: var(--surface); border: 1px solid var(--line); border-radius: 10px; padding: 10px 18px; min-width: 120px; }
.facts dt { font-size: 12px; color: var(--text-3); text-transform: uppercase; letter-spacing: .05em; }
.facts dd { margin: 0; font-size: 24px; font-weight: 650; font-variant-numeric: tabular-nums; }
.board { margin-top: 32px; }
.scroll { overflow-x: auto; background: var(--surface); border: 1px solid var(--line); border-radius: 12px; }
table { border-collapse: collapse; width: 100%; min-width: 980px; font-variant-numeric: tabular-nums; }
table.makers { min-width: 640px; }
table.makers th { cursor: default; padding-bottom: 12px; }
table.makers td { white-space: normal; vertical-align: top; }
table.makers td:first-child { white-space: nowrap; }
table.makers tr.country-start td { border-top: 2px solid var(--line-strong); }
table.makers tbody tr:first-child td { border-top: 0; }
.chip { display: inline-block; margin: 2px 6px 2px 0; padding: 2px 8px; border-radius: 6px; background: var(--track); font-size: 13px; white-space: nowrap; }
th { position: relative; padding: 12px 10px 30px; font-size: 12px; font-weight: 600; color: var(--text-2); text-align: left;
  white-space: nowrap; border-bottom: 1px solid var(--line-strong); cursor: pointer; user-select: none; vertical-align: top; }
th:hover, th:focus-visible { color: var(--text); outline: none; }
th[aria-sort="ascending"]::after { content: " ▲"; font-size: 9px; }
th[aria-sort="descending"]::after { content: " ▼"; font-size: 9px; }
th.plot { cursor: default; min-width: 250px; }
td { padding: 11px 10px; border-bottom: 1px solid var(--line); vertical-align: middle; white-space: nowrap; }
tbody tr:last-child td { border-bottom: 0; }
tbody tr:hover td { background: var(--accent-soft); }
tr.top1 td { background: var(--top); }
.num { text-align: right; }
th.num { text-align: right; }
.muted { color: var(--text-3); }
.strong { font-weight: 650; font-size: 16px; }
.pm { margin-left: 4px; font-size: 12px; font-weight: 400; color: var(--text-3); }
.sub { display: block; font-size: 12px; color: var(--text-3); font-weight: 400; }
.name { font-weight: 600; }
.model .sub { max-width: 260px; overflow: hidden; text-overflow: ellipsis; }
#lb th, #lb td { padding-inline: 8px; }
#lb th, #lb .model { white-space: normal; }
#lb th[data-key="invalid"], #lb th[data-key="truncated"] { width: 86px; }
#lb .model .sub { white-space: nowrap; }
.rank { display: inline-block; min-width: 26px; padding: 1px 7px; border-radius: 999px; text-align: center;
  font-size: 13px; font-weight: 600; background: var(--track); color: var(--text-2); }
tr.top1 .rank { background: var(--accent); color: #fff; }
.badge { display: inline-block; margin-left: 4px; padding: 0 6px; border-radius: 4px; font-size: 10px; font-weight: 700;
  letter-spacing: .04em; line-height: 16px; vertical-align: 2px; color: var(--text-2); border: 1px solid var(--line-strong); cursor: help; }
.flag { color: var(--flag); font-weight: 700; margin-left: 2px; cursor: help; }
#lb .plot { padding-inline: 14px; }
.axis { position: absolute; left: 14px; right: 14px; bottom: 9px; height: 14px; }
.axis span { position: absolute; transform: translateX(-50%); font-size: 10px; font-weight: 400; color: var(--text-3); }
.track { position: relative; height: 22px; background:
  linear-gradient(var(--track), var(--track)) center / 100% 2px no-repeat; border-radius: 4px; }
.track .ci { position: absolute; top: 5px; height: 12px; border-left: 2px solid var(--accent); border-right: 2px solid var(--accent);
  background: linear-gradient(var(--accent), var(--accent)) center / 100% 2px no-repeat; }
.track .ref { position: absolute; top: 0; bottom: 0; width: 1px; background: var(--line-strong); }
.track .dot { position: absolute; top: 5px; width: 12px; height: 12px; margin-left: -6px; border-radius: 50%;
  background: var(--accent); box-shadow: 0 0 0 2px var(--surface); }
.note { margin: 10px 2px 0; font-size: 13px; color: var(--text-3); }
.scroll-hint { margin: 0 2px 8px; }
.cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 14px; margin: 0; }
.cols div { background: var(--surface); border: 1px solid var(--line); border-radius: 10px; padding: 14px 18px; }
.cols dt { font-weight: 650; margin-bottom: 4px; }
.cols dd { margin: 0; color: var(--text-2); font-size: 14px; }
.callout { display: grid; grid-template-columns: 1fr auto; column-gap: 16px; align-items: center; margin: 32px 0 0;
  padding: 18px 22px; background: var(--surface); border: 1px solid var(--line); border-left: 4px solid var(--green);
  border-radius: 10px; color: var(--text); text-decoration: none; }
.callout:hover, .callout:focus-visible { background: var(--accent-soft); outline: none; }
.callout-title { font-size: 17px; font-weight: 650; color: var(--green); }
.callout-text { grid-row: 2; font-size: 14px; color: var(--text-2); }
.callout-arrow { grid-row: 1 / 3; grid-column: 2; font-size: 26px; color: var(--green); }
.method { max-width: 820px; color: var(--text-2); }
.method strong { color: var(--text); }
blockquote { margin: 12px 0 16px; padding: 12px 16px; border-left: 3px solid var(--accent); background: var(--surface);
  border-radius: 0 8px 8px 0; font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 13px; color: var(--text); }
footer { margin-top: 48px; padding-top: 16px; border-top: 1px solid var(--line); font-size: 12px; color: var(--text-3); }
@media (max-width: 640px) { main { padding-top: 28px; } .facts div { min-width: 0; flex: 1 1 40%; } }
</style>
</head>
<body>

""";

    private const string Script = """
<script>
(() => {
  const table = document.getElementById('lb');
  const scroller = table.parentElement;
  const overflowHint = document.getElementById('lb-overflow');
  const observer = new ResizeObserver(() => {
    overflowHint.hidden = scroller.scrollWidth <= scroller.clientWidth;
  });
  observer.observe(scroller);
  observer.observe(table);
  const body = table.tBodies[0];
  const heads = [...table.tHead.rows[0].cells];
  const sortBy = th => {
    const i = heads.indexOf(th);
    const text = th.dataset.key === 'model';
    const lowerIsBetter = ['pos', 'rank', 'spa'].includes(th.dataset.key);
    const cur = th.getAttribute('aria-sort');
    const dir = cur === 'none' ? ((text || lowerIsBetter) ? 1 : -1) : (cur === 'ascending' ? -1 : 1);
    heads.forEach(h => h.dataset.key && h.setAttribute('aria-sort', 'none'));
    th.setAttribute('aria-sort', dir === 1 ? 'ascending' : 'descending');
    const rows = [...body.rows];
    rows.sort((a, b) => {
      const x = a.cells[i].dataset.v, y = b.cells[i].dataset.v;
      return dir * (text ? x.localeCompare(y) : parseFloat(x) - parseFloat(y));
    });
    rows.forEach(r => body.appendChild(r));
  };
  heads.forEach(th => {
    if (!th.dataset.key) return;
    th.addEventListener('click', () => sortBy(th));
    th.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); sortBy(th); } });
  });
})();
</script>

""";
}

/// <summary>Maker, country and GGUF source of a model (model-info.json in the workspace, key = model alias).</summary>
public sealed record ModelInfo(string Maker, string Country, string Gguf)
{
    public static Dictionary<string, ModelInfo> Load(string path)
    {
        var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject root) return map;
        foreach (var (alias, node) in root)
            if (node is System.Text.Json.Nodes.JsonObject o)
                map[alias] = new ModelInfo(o["maker"]?.GetValue<string>() ?? "", o["country"]?.GetValue<string>() ?? "", o["gguf"]?.GetValue<string>() ?? "");
        return map;
    }
}
