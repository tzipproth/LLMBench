using System.Globalization;
using System.Text;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Stats;

public enum CompareKind { BothCorrect, OnlyA, OnlyB, BothWrong, Excluded, OnlyInA, OnlyInB }

public sealed class CompareRow
{
    public required string QuestionId { get; init; }
    public required string VariantId { get; init; }
    public required int Repetition { get; init; }
    public required string Expected { get; init; }
    public ItemRecord? A { get; init; }
    public ItemRecord? B { get; init; }
    public required CompareKind Kind { get; init; }
    public string Key => $"{QuestionId}/{VariantId}";
}

public sealed class CompareResult
{
    public required string NameA { get; init; }
    public required string NameB { get; init; }
    public int BothCorrect { get; set; }
    public int OnlyA { get; set; }
    public int OnlyB { get; set; }
    public int BothWrong { get; set; }
    public int Excluded { get; set; }
    public int OnlyInA { get; set; }
    public int OnlyInB { get; set; }
    public int Paired => BothCorrect + OnlyA + OnlyB + BothWrong;
    /// <summary>Paired test over questions (see <see cref="PairedTest"/>).</summary>
    public PairedTest Test { get; set; } = new(0, 0, 0, 1.0);
    public double PValue => Test.PValue;
    public bool SameQuestionSet { get; set; }
    public RunSummary? SummaryA { get; set; }
    public RunSummary? SummaryB { get; set; }
    public List<CompareRow> Rows { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<ParameterDifference> Differences { get; set; } = [];
}

/// <summary>
/// Paired comparison of two runs over the questions both answered. Each question/variant counts once
/// (repetitions are averaged to a solve rate), so for runs without repetitions this is exactly McNemar's test.
/// </summary>
/// <param name="ABetter">questions where A has the higher solve rate</param>
/// <param name="BBetter">questions where B has the higher solve rate</param>
/// <param name="Ties">questions with equal solve rate</param>
/// <param name="PValue">exact two-sided sign test on ABetter vs. BBetter</param>
public sealed record PairedTest(int ABetter, int BBetter, int Ties, double PValue)
{
    public const double Alpha = 0.05;
    public bool ASignificantlyBetter => PValue < Alpha && ABetter > BBetter;
    public bool BSignificantlyBetter => PValue < Alpha && BBetter > ABetter;
}

/// <summary>Concept 11.3: question-by-question comparison of two runs.</summary>
public static class RunComparer
{
    public static CompareResult Compare(Workspace ws, string nameA, RunResults a, string nameB, RunResults b)
    {
        if (a.Header == null) throw new BenchException($"Run '{nameA}' has no results.");
        if (b.Header == null) throw new BenchException($"Run '{nameB}' has no results.");

        var r = new CompareResult
        {
            NameA = nameA,
            NameB = nameB,
            SummaryA = SummaryBuilder.Rebuild(a),
            SummaryB = SummaryBuilder.Rebuild(b),
            Differences = ParameterDiff.Diff(a.Header.Definition, a.Header, b.Header.Definition, b.Header),
        };

        var qa = QuestionSet.ForRun(ws, a.Header, a.Items);
        var qb = QuestionSet.ForRun(ws, b.Header, b.Items);
        r.SameQuestionSet = qa.Hash == qb.Hash;
        if (!r.SameQuestionSet) r.Warnings.Add("The runs ask different questions (question set differs) - only common items are compared.");
        else if (a.Header.CatalogSha256 != b.Header.CatalogSha256) r.Warnings.Add("Catalog files differ, but the asked questions are identical.");
        if (a.Header.Definition.Repetitions != b.Header.Definition.Repetitions)
            r.Warnings.Add($"Different repetitions ({a.Header.Definition.Repetitions} vs {b.Header.Definition.Repetitions}) - items are paired per repetition.");
        if (a.Summary?.Completed != true) r.Warnings.Add($"Run A '{nameA}' is not complete.");
        if (b.Summary?.Completed != true) r.Warnings.Add($"Run B '{nameB}' is not complete.");

        var mapA = a.Items.GroupBy(i => (i.Key, i.Repetition)).ToDictionary(g => g.Key, g => g.First());
        var mapB = b.Items.GroupBy(i => (i.Key, i.Repetition)).ToDictionary(g => g.Key, g => g.First());
        var keys = mapA.Keys.Union(mapB.Keys)
            .OrderBy(k => mapA.GetValueOrDefault(k)?.Index ?? int.MaxValue)
            .ThenBy(k => mapB.GetValueOrDefault(k)?.Index ?? int.MaxValue);

        foreach (var k in keys)
        {
            var ia = mapA.GetValueOrDefault(k);
            var ib = mapB.GetValueOrDefault(k);
            CompareKind kind;
            if (ia == null) kind = CompareKind.OnlyInB;
            else if (ib == null) kind = CompareKind.OnlyInA;
            else if (!SummaryBuilder.IsScored(ia) || !SummaryBuilder.IsScored(ib)) kind = CompareKind.Excluded;
            else
            {
                bool ca = ia.Status == ItemStatus.Correct, cb = ib.Status == ItemStatus.Correct;
                kind = (ca, cb) switch
                {
                    (true, true) => CompareKind.BothCorrect,
                    (true, false) => CompareKind.OnlyA,
                    (false, true) => CompareKind.OnlyB,
                    _ => CompareKind.BothWrong,
                };
            }
            var any = ia ?? ib!;
            r.Rows.Add(new CompareRow
            {
                QuestionId = any.QuestionId, VariantId = any.VariantId, Repetition = any.Repetition,
                Expected = ia?.Expected ?? ib!.Expected, A = ia, B = ib, Kind = kind,
            });
            switch (kind)
            {
                case CompareKind.BothCorrect: r.BothCorrect++; break;
                case CompareKind.OnlyA: r.OnlyA++; break;
                case CompareKind.OnlyB: r.OnlyB++; break;
                case CompareKind.BothWrong: r.BothWrong++; break;
                case CompareKind.Excluded: r.Excluded++; break;
                case CompareKind.OnlyInA: r.OnlyInA++; break;
                case CompareKind.OnlyInB: r.OnlyInB++; break;
            }
        }
        r.Test = Paired(a.Items, b.Items);
        return r;
    }

    /// <summary>Paired per-question test (scored items only; errors/skipped are ignored).</summary>
    public static PairedTest Paired(IReadOnlyList<ItemRecord> a, IReadOnlyList<ItemRecord> b)
    {
        static Dictionary<string, double> Rates(IReadOnlyList<ItemRecord> items) =>
            items.Where(SummaryBuilder.IsScored).GroupBy(i => i.Key)
                .ToDictionary(g => g.Key, g => (double)g.Count(i => i.Status == ItemStatus.Correct) / g.Count());
        var ra = Rates(a);
        var rb = Rates(b);
        int aBetter = 0, bBetter = 0, ties = 0;
        foreach (var (key, va) in ra)
        {
            if (!rb.TryGetValue(key, out var vb)) continue;
            if (Math.Abs(va - vb) < 1e-9) ties++;
            else if (va > vb) aBetter++;
            else bBetter++;
        }
        return new PairedTest(aBetter, bBetter, ties, McNemarExact(aBetter, bBetter));
    }

    /// <summary>Two-sided exact binomial test with p = 0.5 on the discordant pairs.</summary>
    public static double McNemarExact(int b, int c)
    {
        int n = b + c;
        if (n == 0) return 1.0;
        int k = Math.Min(b, c);
        double sum = 0;
        for (int i = 0; i <= k; i++) sum += Math.Exp(LogChoose(n, i) - n * Math.Log(2));
        return Math.Min(1.0, 2 * sum);
    }

    private static double LogChoose(int n, int k)
    {
        double s = 0;
        for (int i = 1; i <= k; i++) s += Math.Log(n - k + i) - Math.Log(i);
        return s;
    }

    public static string Format(CompareResult r, bool listDifferingItems = true)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"A: {r.NameA}   accuracy {TextReport.Pct(r.SummaryA?.Accuracy)} ({r.SummaryA?.Correct}/{r.SummaryA?.Scored})");
        sb.AppendLine($"B: {r.NameB}   accuracy {TextReport.Pct(r.SummaryB?.Accuracy)} ({r.SummaryB?.Correct}/{r.SummaryB?.Scored})");
        sb.AppendLine();
        sb.AppendLine($"Paired items: {r.Paired}   both correct {r.BothCorrect}   only A {r.OnlyA}   only B {r.OnlyB}   both wrong {r.BothWrong}");
        if (r.Excluded + r.OnlyInA + r.OnlyInB > 0)
            sb.AppendLine($"Not paired: {r.Excluded} error/skipped, {r.OnlyInA} only in A, {r.OnlyInB} only in B");
        var t = r.Test;
        var verdict = t.ABetter + t.BBetter == 0 ? "no question with a different result"
            : t.ASignificantlyBetter ? "A is significantly better"
            : t.BSignificantlyBetter ? "B is significantly better"
            : "difference is not significant (could be chance)";
        sb.AppendLine($"Paired test over questions (McNemar): A better on {t.ABetter}, B better on {t.BBetter}, equal on {t.Ties}: p = {t.PValue.ToString("0.000", inv)}  ->  {verdict}");
        foreach (var w in r.Warnings) sb.AppendLine("WARNING: " + w);

        sb.AppendLine();
        if (r.Differences.Count == 0) sb.AppendLine("Parameters: identical (apart from name).");
        else
        {
            sb.AppendLine("Parameters that differ:");
            int w = r.Differences.Max(d => d.Name.Length) + 2;
            foreach (var d in r.Differences)
                sb.AppendLine($"  {d.Name.PadRight(w)} A: {Shorten(d.A, 60),-62} B: {Shorten(d.B, 60)}");
        }

        if (listDifferingItems)
        {
            var diff = r.Rows.Where(x => x.Kind is CompareKind.OnlyA or CompareKind.OnlyB).ToList();
            if (diff.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Items with different outcome:");
                foreach (var x in diff)
                    sb.AppendLine($"  {(x.Kind == CompareKind.OnlyA ? "only A" : "only B")}  {x.Key,-28} r{x.Repetition}  expected {Shorten(x.Expected, 30),-32} A: {Shorten(x.A?.ExtractedAnswer, 18),-20} B: {Shorten(x.B?.ExtractedAnswer, 18)}");
            }
        }
        return sb.ToString();
    }

    private static string Shorten(string? s, int max)
    {
        var t = (s ?? "-").Replace("\r", " ").Replace("\n", " ");
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
