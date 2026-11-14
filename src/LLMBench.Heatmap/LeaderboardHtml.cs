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

    public static string Render(LeaderboardGroup group, IList<LeaderboardEntry> shown, string heatmapFile, string gpu, int vramGb)
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
        sb.Append($"<p class=\"lede\">Which local LLM thinks best on a graphics card with {vramGb} GB? Every model here fits completely into {vramGb} GB of VRAM, " +
                  $"and all {rows.Count} answer the same {group.QuestionCount} reasoning questions on the same {H(gpu)}. " +
                  "Spatial and physical reasoning, keeping track of objects, altered classic puzzles, logic, math, time and social reasoning: " +
                  "tasks where a model has to think, not recall.</p>\n");
        sb.Append("<dl class=\"facts\">");
        Fact(sb, first.Header.CatalogQuestions.ToString(Inv), "questions");
        Fact(sb, group.QuestionCount.ToString(Inv), "question variants");
        Fact(sb, rows.Count.ToString(Inv), "models");
        Fact(sb, gpu, $"GPU, {vramGb} GB VRAM");
        sb.Append("</dl>\n</header>\n");

        // ---- table -------------------------------------------------------------------------------------
        sb.Append("<section class=\"board\">\n<div class=\"scroll\"><table id=\"lb\">\n<thead><tr>");
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
            var file = Path.GetFileNameWithoutExtension(e.ModelFile);
            Td(sb, $"<span class=\"name\">{H(e.Model)}{(flagged ? "<sup class=\"flag\" title=\"" + H(e.UsabilityNote) + "\">†</sup>" : "")}</span>" +
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
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table></div>\n");
        if (rows.Any(e => e.UsabilityNote.Length > 0))
            sb.Append("<p class=\"note\"><span class=\"flag\">†</span> 5 % or more of the answers were cut off at the output limit or had no readable final answer. " +
                      "They count as wrong, so the model may do better with a larger limit.</p>\n");
        sb.Append("<p class=\"note\">Click a column header to sort. Rows with rank 1 are highlighted.</p>\n</section>\n");

        // ---- explanations ------------------------------------------------------------------------------
        sb.Append("<section class=\"explain\">\n<h2>How to read the columns</h2>\n<dl class=\"cols\">\n");
        Col(sb, "Rank", "1 + the number of models that are <em>significantly</em> better. Two models share a rank when the difference between them " +
                        "could be chance: every pair of models is compared question by question (paired McNemar test, p &lt; 0.05).");
        Col(sb, "Model", "The model name and below it the exact model file (quantization), so the result can be reproduced.");
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
                                    "All models run completely in VRAM, so another graphics card mostly makes every model faster or slower by a similar factor.");
        Col(sb, "Accuracy", "The share of correct answers. Below: the 95 % range (Wilson interval). It puts the models in the same order as the strength, " +
                            "but squeezes the top: near 100 % every further percent is much harder to gain than in the middle, the strength shows that.");
        Col(sb, "Correct", "Correct answers out of all scored answers.");
        Col(sb, "s / answer", $"Mean time per answer in seconds on the {H(gpu)}. Depends on the speed of the model and on how much it writes before it answers.");
        Col(sb, "Quality", "Right minus wrong answers per 100 answers: what the answers are worth if you trust them. No usable answer counts 0. " +
                           "A wrong multiple-choice answer with <em>k</em> options costs only 1/(<em>k</em>−1), so guessing gains nothing. " +
                           "A model that is often wrong drops further here than in accuracy. Quality per time is the same value per minute instead of per answer.");
        sb.Append("</dl>\n");

        sb.Append($"<a class=\"callout\" href=\"{H(heatmapFile)}\"><span class=\"callout-title\">Every single answer of every model</span>" +
                  "<span class=\"callout-text\">Heatmap of all results: one square per question and model, with the question, the answer and the time on hover.</span>" +
                  "<span class=\"callout-arrow\" aria-hidden=\"true\">→</span></a>\n");
        sb.Append("<h2>How the benchmark works</h2>\n<div class=\"method\">\n");
        sb.Append($"<p><strong>Questions.</strong> {first.Header.CatalogQuestions} questions, many of them in two forms (free answer and multiple choice), " +
                  $"{group.QuestionCount} variants in total. Some are well-known puzzles with a twist that changes the answer, so a memorized solution is wrong. " +
                  "The options of multiple-choice questions are shuffled the same way for every model.</p>\n");
        sb.Append($"<p><strong>Same conditions for every model.</strong> Each model runs locally in llama-server on one {H(gpu)}, completely in its {vramGb} GB of VRAM, with temperature {N(def.Temperature)}, seed {N(def.Seed)} " +
                  $"and at most {def.MaxTokens?.ToString("#,0", Inv) ?? "unlimited"} output tokens per answer. Models with a thinking switch run with thinking off, the others as they come. " +
                  "Every question is sent on its own, with this system prompt:</p>\n");
        if (first.Header.SystemPrompt is { } sp) sb.Append($"<blockquote>{H(sp)}</blockquote>\n");
        sb.Append("<p><strong>Scoring.</strong> Only the final answer counts: the line starting with “Final Answer:”, compared with the solution " +
                  "(numbers as numbers, options by letter or text). The reasoning before it is not graded. Answers cut off at the output limit count as wrong.</p>\n");
        if (cal != null)
            sb.Append($"<p><strong>Strength scale.</strong> The difficulty of every question was estimated once from the answers of all {cal.Runs.Count} models (Rasch model with a guessing floor for " +
                      $"multiple choice) on {cal.CreatedAt:yyyy-MM-dd} and is now fixed. Models added later are placed on the same scale without changing the values above, and {H(reference ?? "the reference model")} stays the reference (= 1×).</p>\n");
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
th { position: relative; padding: 12px 10px 30px; font-size: 12px; font-weight: 600; color: var(--text-2); text-align: left;
  white-space: nowrap; border-bottom: 1px solid var(--line-strong); cursor: pointer; user-select: none; vertical-align: top; }
th:hover, th:focus-visible { color: var(--text); outline: none; }
th[aria-sort="ascending"]::after { content: " ▲"; font-size: 9px; }
th[aria-sort="descending"]::after { content: " ▼"; font-size: 9px; }
th.plot { cursor: default; min-width: 300px; padding-left: 14px; padding-right: 14px; }
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
.rank { display: inline-block; min-width: 26px; padding: 1px 7px; border-radius: 999px; text-align: center;
  font-size: 13px; font-weight: 600; background: var(--track); color: var(--text-2); }
tr.top1 .rank { background: var(--accent); color: #fff; }
.flag { color: var(--flag); font-weight: 700; margin-left: 2px; cursor: help; }
td.plot { padding-left: 14px; padding-right: 14px; }
.axis { position: absolute; left: 14px; right: 14px; bottom: 9px; height: 14px; }
.axis span { position: absolute; transform: translateX(-50%); font-size: 11px; font-weight: 400; color: var(--text-3); }
.track { position: relative; height: 22px; background:
  linear-gradient(var(--track), var(--track)) center / 100% 2px no-repeat; border-radius: 4px; }
.track .ci { position: absolute; top: 5px; height: 12px; border-left: 2px solid var(--accent); border-right: 2px solid var(--accent);
  background: linear-gradient(var(--accent), var(--accent)) center / 100% 2px no-repeat; }
.track .ref { position: absolute; top: 0; bottom: 0; width: 1px; background: var(--line-strong); }
.track .dot { position: absolute; top: 5px; width: 12px; height: 12px; margin-left: -6px; border-radius: 50%;
  background: var(--accent); box-shadow: 0 0 0 2px var(--surface); }
.note { margin: 10px 2px 0; font-size: 13px; color: var(--text-3); }
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
