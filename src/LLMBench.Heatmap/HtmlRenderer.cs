using System.Globalization;
using System.Text;
using System.Text.Json;
using LLMBench.Core.Stats;

namespace LLMBench.Heatmap;

/// <summary>
/// The same picture as a self-contained HTML page: run names and accuracy on the left, tier labels on top,
/// zoom, and a tooltip per cell (question, status, answer, tokens, seconds). No external files or libraries.
/// </summary>
public static class HtmlRenderer
{
    public static string Render(HeatmapData d)
    {
        var inv = CultureInfo.InvariantCulture;
        var xs = PngRenderer.ColumnX(d, out int width);
        var kinds = Enum.GetValues<CellKind>();
        var data = new
        {
            title = d.Group.Title,
            pitch = PngRenderer.Pitch,
            square = PngRenderer.Square,
            width,
            xs,
            colors = kinds.Select(k => "#" + HeatmapData.Color(k).ToString("X6")).ToArray(),
            legend = kinds.Select(d.Legend).ToArray(),
            rows = d.Rows.Select(e => new
            {
                name = e.RunName,
                acc = TextReport.Pct(e.Summary.Accuracy),
                net = e.Summary.Budget != null ? TextReport.Pct(e.Summary.Budget.NetScore) : "-",
                score = e.Ability != null ? e.Ability.Score.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                        (e.Ability.Strength != null ? $"  {Rasch.FormatStrength(e.Ability.Strength)}×" : "") : "",
                note = e.UsabilityNote,
            }).ToArray(),
            cols = d.Columns.Select(c => new
            {
                id = c.QuestionId + "/" + c.VariantId,
                tier = c.Tier,
                rate = Math.Round(c.SolveRate, 3),
                q = Short(c.Question, 400),
            }).ToArray(),
            cells = d.Cells.Select(row => row.Select(i => i == null ? null : new object[]
            {
                (int)d.Kind(i),
                i.CompletionTokens ?? 0,
                Math.Round(i.ElapsedSeconds, 1),
                Short(i.ExtractedAnswer ?? (i.Reason != null ? "(" + i.Reason + ")" : ""), 80),
                Short(i.Expected ?? "", 60),
            }).ToArray()).ToArray(),
        };
        var json = JsonSerializer.Serialize(data); // escapes < > & so it is safe inside <script>

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>LLMBench heatmap</title>
<style>
 body{margin:0;padding:16px;background:#141414;color:#ddd;font:13px system-ui,sans-serif}
 h1{font-size:16px;margin:0 0 4px} .sub{color:#999;margin-bottom:10px}
 .bar{margin:8px 0} .wrap{overflow-x:auto} canvas{display:block}
 #tip{position:fixed;pointer-events:none;background:#222;border:1px solid #555;padding:6px 8px;max-width:520px;
      white-space:pre-wrap;display:none;font-size:12px;line-height:1.35;color:#eee}
 .legend span{display:inline-block;margin-right:14px} .legend i{display:inline-block;width:10px;height:10px;margin-right:4px;vertical-align:-1px}
</style></head><body>
<h1>LLMBench – result per question</h1>
<div class="sub" id="sub"></div>
<div class="bar">Zoom <select id="zoom"><option>1</option><option selected>2</option><option>3</option><option>4</option></select>
 &nbsp; one square per question variant, columns grouped by tier (easy → hard within each tier), rows = runs (best first);
 bottom row: solve rate over all runs (white = all solve, dark = none)</div>
<div class="legend" id="legend"></div>
<div class="bar">Gray run names mark a warning, for example when truncated and invalid answers together account for at least 5 %.</div>
<div class="wrap"><canvas id="c"></canvas></div>
<div id="tip"></div>
<script>
const D =
""");
        sb.Append(json).Append(';');
        sb.Append("""

const LABEL = 300, TOP = 22, GAP = 6;
const cv = document.getElementById('c'), ctx = cv.getContext('2d'), tip = document.getElementById('tip');
document.getElementById('sub').textContent = D.title;
document.getElementById('legend').innerHTML = D.colors.map((c, k) => `<span><i style="background:${c}"></i>${D.legend[k]}</span>`).join('')
  + '<span><i style="background:linear-gradient(90deg,#3c3c3c,#fff)"></i>solve rate 0 → 100 %</span>';
let z = 2;
function gray(r){ const v = Math.round(0x3c + (0xff - 0x3c) * r); return `rgb(${v},${v},${v})`; }
function draw(){
  const p = D.pitch * z, rows = D.rows.length;
  cv.width = LABEL + D.width * z + 10;
  cv.height = TOP + rows * p + GAP * z + p + 10;
  ctx.fillStyle = '#141414'; ctx.fillRect(0, 0, cv.width, cv.height);
  ctx.font = '12px system-ui,sans-serif'; ctx.textBaseline = 'middle';
  let last = -1;
  D.cols.forEach((c, i) => { if (c.tier !== last) { ctx.fillStyle = '#aaa'; ctx.fillText('T' + c.tier, LABEL + D.xs[i] * z, TOP / 2); last = c.tier; } });
  D.rows.forEach((r, ri) => {
    const y = TOP + ri * p;
    ctx.fillStyle = r.note ? '#888' : '#ddd';
    ctx.fillText(`${r.name}  ${r.acc}${r.score ? '  ' + r.score : ''}`, 4, y + p / 2, LABEL - 8);
    D.cells[ri].forEach((cell, ci) => {
      ctx.fillStyle = cell ? D.colors[cell[0]] : D.colors[0];
      ctx.fillRect(LABEL + (D.xs[ci] + 1) * z, y + z, D.square * z, D.square * z);
    });
  });
  const cy = TOP + rows * p + GAP * z;
  ctx.fillStyle = '#ddd'; ctx.fillText('solve rate (all runs)', 4, cy + p / 2, LABEL - 8);
  D.cols.forEach((c, ci) => { ctx.fillStyle = gray(c.rate); ctx.fillRect(LABEL + (D.xs[ci] + 1) * z, cy + z, D.square * z, D.square * z); });
}
function colAt(x){
  const u = (x - LABEL) / z; let lo = 0, hi = D.xs.length - 1, best = -1;
  while (lo <= hi) { const m = (lo + hi) >> 1; if (D.xs[m] <= u) { best = m; lo = m + 1; } else hi = m - 1; }
  return best >= 0 && u < D.xs[best] + D.pitch ? best : -1;
}
cv.addEventListener('mousemove', ev => {
  const b = cv.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top, p = D.pitch * z;
  const ci = colAt(x), ri = Math.floor((y - TOP) / p), cy = TOP + D.rows.length * p + GAP * z;
  if (ci < 0) { tip.style.display = 'none'; return; }
  const c = D.cols[ci]; let t = `${c.id}  (tier ${c.tier}, solved by ${Math.round(c.rate * 100)} % of the runs)\n\n${c.q}`;
  if (ri >= 0 && ri < D.rows.length) {
    const cell = D.cells[ri][ci], r = D.rows[ri];
    t = `${r.name}\n` + (cell ? `${D.legend[cell[0]]}\nanswer: ${cell[3]}\nexpected: ${cell[4]}\n${cell[1]} tokens, ${cell[2]} s\n\n` : 'not answered\n\n') + t;
  } else if (!(y >= cy && y < cy + p)) { tip.style.display = 'none'; return; }
  tip.textContent = t; tip.style.display = 'block';
  tip.style.left = Math.min(ev.clientX + 14, innerWidth - 540) + 'px'; tip.style.top = (ev.clientY + 14) + 'px';
});
cv.addEventListener('mouseleave', () => tip.style.display = 'none');
document.getElementById('zoom').addEventListener('change', e => { z = +e.target.value; draw(); });
draw();
</script></body></html>
""");
        return sb.ToString();
    }

    private static string Short(string s, int n)
    {
        s = s.Replace("\r", "");
        return s.Length <= n ? s : s[..n] + "…";
    }
}
