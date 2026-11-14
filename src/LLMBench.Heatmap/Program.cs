using System.Globalization;
using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Stats;
using LLMBench.Heatmap;

// llmbench-heatmap: one picture of all results - a row per run, a 4x4 square per question.
const string Help = """
llmbench-heatmap - result of every question in every run as a picture

Usage: llmbench-heatmap [options]
  --workspace DIR     LLMBench workspace (default: search upwards from the current folder)
  --name TEXT         only runs whose name contains TEXT (e.g. v4-)
  --group HASH        leaderboard group (default: the group with the most matching runs)
  --completed         only completed runs (default: incomplete runs too, missing answers dark grey)
  --out FILE          PNG file (default: docs/heatmap.png); an HTML page with the same name is written too,
                      and leaderboard.html (the public leaderboard with explanations) in the same folder
  --gpu NAME          graphics card the runs were made on, shown on the leaderboard (default RTX 5070 Ti)
  --vram GB           its memory; every model has to fit into it completely (default 16)
  --no-html           only the PNG
  --speed seconds|tokens   what decides fast / slow (default seconds)
  --fast N --slow N   correct within N is "fast", above N "slow" (default 10 / 60 s, or 1000 / 4000 tokens)
  --order difficulty|catalog   columns per tier from easy to hard (default), or catalog order
  --tier-gap N        extra pixels between tiers (default 4)
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var a = args.ToList();
string? Opt(string name)
{
    int i = a.IndexOf("--" + name);
    return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
}
bool Flag(string name) => a.Contains("--" + name);
if (Flag("help") || Flag("h")) { Console.WriteLine(Help); return 0; }

try
{
    var ws = Workspace.Find(Opt("workspace"));
    var opt = new HeatmapOptions { SpeedBy = Opt("speed") ?? "seconds", Order = Opt("order") ?? "difficulty" };
    if (opt.SpeedBy is not ("seconds" or "tokens")) throw new BenchException("--speed must be seconds or tokens.");
    if (opt.Order is not ("difficulty" or "catalog")) throw new BenchException("--order must be difficulty or catalog.");
    if (opt.SpeedBy == "tokens") { opt.Fast = 1000; opt.Slow = 4000; }
    if (Opt("fast") is { } f) opt.Fast = double.Parse(f, CultureInfo.InvariantCulture);
    if (Opt("slow") is { } s) opt.Slow = double.Parse(s, CultureInfo.InvariantCulture);
    if (Opt("tier-gap") is { } g) PngRenderer.TierGap = int.Parse(g, CultureInfo.InvariantCulture);

    var name = Opt("name");
    var group = Opt("group");
    var candidates = Leaderboard.Build(ws)
        .Where(gr => group == null || gr.QuestionSetHash.StartsWith(group, StringComparison.OrdinalIgnoreCase))
        .Select(gr => (Group: gr, Entries: gr.Entries
            .Where(e => name == null || e.RunName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Where(e => !Flag("completed") || e.State == RunState.Completed).ToList()))
        .Where(x => x.Entries.Count > 0)
        .OrderByDescending(x => x.Entries.Count)
        .ToList();
    if (candidates.Count == 0) throw new BenchException("No matching runs (see 'llmbench leaderboard').");
    var (grp, entries) = candidates[0];

    var data = HeatmapData.Build(grp, entries, opt);
    var img = PngRenderer.Render(data);

    var outPng = Path.GetFullPath(Opt("out") ?? Path.Combine(ws.Root, "docs", "heatmap.png"));
    Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
    img.SavePng(outPng);
    Console.WriteLine($"{grp.Title}");
    Console.WriteLine($"{data.Rows.Count} run(s) x {data.Columns.Count} question variants -> {img.Width} x {img.Height} px: {outPng}");
    if (img.Width > 3840) Console.WriteLine("Note: wider than a 4K screen (3840 px); --tier-gap 0 saves a little.");
    if (!Flag("no-html"))
    {
        var outHtml = Path.ChangeExtension(outPng, ".html");
        File.WriteAllText(outHtml, HtmlRenderer.Render(data));
        Console.WriteLine($"HTML with names and tooltips: {outHtml}");
        var outBoard = Path.Combine(Path.GetDirectoryName(outPng)!, "leaderboard.html");
        File.WriteAllText(outBoard, LeaderboardHtml.Render(grp, entries, Path.GetFileName(outHtml),
            Opt("gpu") ?? "RTX 5070 Ti", int.Parse(Opt("vram") ?? "16", CultureInfo.InvariantCulture)));
        Console.WriteLine($"Leaderboard page: {outBoard}");
    }
    Console.WriteLine();
    foreach (var k in Enum.GetValues<CellKind>())
        Console.WriteLine($"  #{HeatmapData.Color(k):X6}  {data.Legend(k)}");
    Console.WriteLine("  bottom row: solve rate over the shown runs, dark = none solves, white = all solve");
    Console.WriteLine();
    foreach (var e in data.Rows)
        Console.WriteLine($"  row {e.Position,2}: {e.RunName} {TextReport.Pct(e.Summary.Accuracy)}{(e.Ability != null ? $"  score {e.Ability.Score:0}" : "")}{(e.Ability?.Strength != null ? $"  strength {Rasch.FormatStrength(e.Ability.Strength)}×" : "")}{(e.UsabilityNote.Length > 0 ? " (" + e.UsabilityNote + ")" : "")}");
    return 0;
}
catch (Exception ex) when (ex is BenchException or IOException or FormatException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
