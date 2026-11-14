using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Stats;

/// <summary>Ability of one run on the calibrated scale.</summary>
public sealed record Ability(double Theta, double StandardError, int Responses, double? ReferenceTheta = null)
{
    public double Score => Rasch.Score(Theta);
    /// <summary>Half width of the 95 % range in score points.</summary>
    public double Score95 => 1.96 * Rasch.PointsPerLogit * StandardError;

    /// <summary>
    /// How many times the odds (correct : wrong) of solving a question compared with the reference run of the
    /// calibration (Bradley-Terry strength, a ratio scale: 84 vs 35 = 2.4 times the odds). Null without a reference.
    /// </summary>
    public double? Strength => ReferenceTheta is { } r ? Math.Exp(Theta - r) : null;
    public double? StrengthLow => ReferenceTheta is { } r ? Math.Exp(Theta - 1.96 * StandardError - r) : null;
    public double? StrengthHigh => ReferenceTheta is { } r ? Math.Exp(Theta + 1.96 * StandardError - r) : null;
}

/// <summary>Difficulty of one question/variant, estimated once from reference runs and then kept fixed.</summary>
public sealed class CalibrationItem
{
    public string Key { get; set; } = "";
    public int? Tier { get; set; }
    /// <summary>Difficulty in logits (higher = harder); the mean over all items is 0.</summary>
    public double Difficulty { get; set; }
    /// <summary>Guessing floor: 1/options for choice questions, else 0.</summary>
    public double Guess { get; set; }
    public int Responses { get; set; }
    public int Correct { get; set; }
}

public sealed class CalibrationRun
{
    public string Run { get; set; } = "";
    public string Model { get; set; } = "";
    public double Theta { get; set; }
    public double StandardError { get; set; }
    public double Score { get; set; }
}

/// <summary>
/// The frozen difficulties of one question set (stored in calibrations/). New runs are placed on the scale with these
/// fixed difficulties, so the scores of earlier runs never change; a new catalog version gets a new calibration.
/// </summary>
public sealed class Calibration
{
    public int SchemaVersion { get; set; } = 1;
    public string QuestionSetHash { get; set; } = "";
    public string CatalogFile { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Scale { get; set; } = FormattableString.Invariant(
        $"score = {Rasch.ScoreBase} + {Rasch.PointsPerLogit:0.0} * theta; P(correct) = guess + (1 - guess) / (1 + exp(difficulty - theta))");
    /// <summary>Run whose ability is strength 1 (fixed for good, like the difficulties); null = no strength column.</summary>
    public string? ReferenceRun { get; set; }
    public double? ReferenceTheta { get; set; }
    public List<CalibrationRun> Runs { get; set; } = [];
    public List<CalibrationItem> Items { get; set; } = [];

    public static string Dir(Workspace ws) => Path.Combine(ws.Root, "calibrations");

    public static string DefaultPath(Workspace ws, string catalogFile, string hash) =>
        Path.Combine(Dir(ws), $"{Path.GetFileNameWithoutExtension(catalogFile)}_{hash[..Math.Min(8, hash.Length)]}.json");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Json.Serialize(this));
    }

    public static Calibration Load(string path) =>
        Json.Deserialize<Calibration>(File.ReadAllText(path)) ?? throw new BenchException($"Empty calibration file {path}.");

    /// <summary>The calibration for a question set, or null (newest file wins if there are several).</summary>
    public static Calibration? FindFor(Workspace ws, string questionSetHash)
    {
        var dir = Dir(ws);
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "*.json")
            .Select(f => { try { return Load(f); } catch (Exception) { return null; } })
            .Where(c => c != null && c.QuestionSetHash == questionSetHash)
            .OrderByDescending(c => c!.CreatedAt)
            .FirstOrDefault();
    }
}

/// <summary>
/// Rasch model (item response theory) with a fixed guessing floor for choice questions:
/// P(correct) = c + (1 - c) * sigmoid(theta - b), theta = ability of the run, b = difficulty of the item,
/// c = 1/options (choice) or 0 (free). Unlike accuracy, equal score differences mean the same everywhere on the scale,
/// hard items count more, easy items and guessing count little. Weak normal priors keep the estimates finite for
/// items solved by all or none and for runs that solve (almost) everything.
/// </summary>
public static class Rasch
{
    public const double ScoreBase = 1000;
    /// <summary>Elo convention: 400 points = factor 10 in the odds.</summary>
    public static readonly double PointsPerLogit = 400 / Math.Log(10);
    public const double ThetaPriorSd = 3.0;
    public const double DifficultyPriorSd = 2.0;
    private const int Iterations = 500;

    public static double Score(double theta) => ScoreBase + PointsPerLogit * theta;

    /// <summary>"84", "7.8", "0.38": two significant digits are enough for a factor.</summary>
    public static string FormatStrength(double? s) => s switch
    {
        null => "",
        // thresholds just below 10 and 1, so that 9.97 shows as "10" and 0.9999 as "1.0"
        >= 9.95 => s.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        >= 0.995 => s.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        _ => s.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
    };

    public static double GuessOf(ItemRecord i) =>
        string.Equals(i.VariantType, "choice", StringComparison.OrdinalIgnoreCase) && i.OptionCount > 1 ? 1.0 / i.OptionCount : 0.0;

    /// <summary>1 = correct, 0 = incorrect/invalid/truncated (as in accuracy), null = not scored (error, skipped).</summary>
    public static int? Response(ItemRecord i) => i.Status switch
    {
        ItemStatus.Correct => 1,
        ItemStatus.Incorrect or ItemStatus.Invalid or ItemStatus.Truncated => 0,
        _ => null,
    };

    /// <summary>Estimates abilities and item difficulties together from reference runs.</summary>
    public static Calibration Calibrate(IReadOnlyList<(string Run, string Model, IReadOnlyList<ItemRecord> Items)> runs,
                                        string questionSetHash, string catalogFile, string? referenceRun = null)
    {
        if (runs.Count < 2) throw new BenchException("A calibration needs at least two runs.");
        if (referenceRun != null && !runs.Any(r => r.Run == referenceRun))
            throw new BenchException($"Reference run '{referenceRun}' is not among the calibration runs.");
        var items = new List<CalibrationItem>();
        var index = new Dictionary<string, int>();
        var obs = new List<(int Run, int Item, int X)>();
        for (int r = 0; r < runs.Count; r++)
            foreach (var it in runs[r].Items)
            {
                if (Response(it) is not { } x) continue;
                if (!index.TryGetValue(it.Key, out var k))
                {
                    k = items.Count;
                    index[it.Key] = k;
                    items.Add(new CalibrationItem { Key = it.Key, Tier = it.Tier, Guess = GuessOf(it) });
                }
                obs.Add((r, k, x));
                items[k].Responses++;
                items[k].Correct += x;
            }

        var theta = new double[runs.Count];
        var b = new double[items.Count];
        var guess = items.Select(i => i.Guess).ToArray();
        var byRun = obs.GroupBy(o => o.Run).ToDictionary(g => g.Key, g => g.Select(o => (o.Item, o.X)).ToArray());
        var byItem = obs.GroupBy(o => o.Item).ToDictionary(g => g.Key, g => g.Select(o => (o.Run, o.X)).ToArray());

        for (int iter = 0; iter < Iterations; iter++)
        {
            for (int r = 0; r < theta.Length; r++)
                if (byRun.TryGetValue(r, out var rs)) theta[r] = NewtonTheta(theta[r], rs.Select(o => (b[o.Item], guess[o.Item], o.X)));
            for (int k = 0; k < b.Length; k++)
                b[k] = NewtonDifficulty(b[k], byItem[k].Select(o => (theta[o.Run], o.X)), guess[k]);
            // identify the scale: mean difficulty 0 (shifting both keeps all probabilities unchanged)
            double mean = b.Average();
            for (int k = 0; k < b.Length; k++) b[k] -= mean;
            for (int r = 0; r < theta.Length; r++) theta[r] -= mean;
        }

        for (int k = 0; k < items.Count; k++) items[k].Difficulty = Math.Round(b[k], 4);
        var cal = new Calibration { QuestionSetHash = questionSetHash, CatalogFile = catalogFile, CreatedAt = DateTime.Now, Items = items };
        for (int r = 0; r < runs.Count; r++)
        {
            var a = Estimate(cal, runs[r].Items);
            cal.Runs.Add(new CalibrationRun
            {
                Run = runs[r].Run, Model = runs[r].Model,
                Theta = Math.Round(a?.Theta ?? double.NaN, 4), StandardError = Math.Round(a?.StandardError ?? double.NaN, 4),
                Score = Math.Round(a?.Score ?? double.NaN, 1),
            });
        }
        if (referenceRun != null)
        {
            cal.ReferenceRun = referenceRun;
            cal.ReferenceTheta = cal.Runs.Single(r => r.Run == referenceRun).Theta;
        }
        return cal;
    }

    /// <summary>Ability of a run with the fixed difficulties of a calibration; items unknown to it are ignored.</summary>
    public static Ability? Estimate(Calibration cal, IReadOnlyList<ItemRecord> items)
    {
        var known = cal.Items.ToDictionary(i => i.Key);
        var data = items
            .Select(i => (Item: known.GetValueOrDefault(i.Key), X: Response(i)))
            .Where(d => d.Item != null && d.X != null)
            .Select(d => (d.Item!.Difficulty, d.Item.Guess, d.X!.Value))
            .ToArray();
        if (data.Length == 0) return null;
        double theta = 0;
        for (int iter = 0; iter < 100; iter++)
        {
            double next = NewtonTheta(theta, data);
            if (Math.Abs(next - theta) < 1e-9) { theta = next; break; }
            theta = next;
        }
        double info = 1 / (ThetaPriorSd * ThetaPriorSd) + data.Sum(d => Information(theta, d.Difficulty, d.Guess));
        return new Ability(theta, 1 / Math.Sqrt(info), data.Length, cal.ReferenceTheta);
    }

    private static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

    // p, dp/dtheta for one item
    private static (double P, double D) Prob(double theta, double b, double c)
    {
        double s = Sigmoid(theta - b);
        return (c + (1 - c) * s, (1 - c) * s * (1 - s));
    }

    private static double Information(double theta, double b, double c)
    {
        var (p, d) = Prob(theta, b, c);
        return d * d / (p * (1 - p));
    }

    // one Newton (Fisher scoring) step for theta with prior N(0, ThetaPriorSd)
    private static double NewtonTheta(double theta, IEnumerable<(double B, double C, int X)> data)
    {
        double g = -theta / (ThetaPriorSd * ThetaPriorSd), h = 1 / (ThetaPriorSd * ThetaPriorSd);
        foreach (var (b, c, x) in data)
        {
            var (p, d) = Prob(theta, b, c);
            g += (x - p) / (p * (1 - p)) * d;
            h += d * d / (p * (1 - p));
        }
        return theta + g / h;
    }

    // one Newton step for b with prior N(0, DifficultyPriorSd); dp/db = -dp/dtheta
    private static double NewtonDifficulty(double b, IEnumerable<(double Theta, int X)> data, double c)
    {
        double g = -b / (DifficultyPriorSd * DifficultyPriorSd), h = 1 / (DifficultyPriorSd * DifficultyPriorSd);
        foreach (var (theta, x) in data)
        {
            var (p, d) = Prob(theta, b, c);
            g -= (x - p) / (p * (1 - p)) * d;
            h += d * d / (p * (1 - p));
        }
        return b + g / h;
    }
}
