using System.Text.Json;
using System.Text.RegularExpressions;
using LLMBench.Core.Scoring;

namespace LLMBench.Core.Catalog;

public static partial class CatalogLoader
{
    public const int MaxOptions = 26;
    /// <summary>Highest allowed tier (1 = easy anchor ... 8 = object-frame round).</summary>
    public const int MaxTier = 9;

    public static readonly string[] Kinds = ["original", "altered", "distractor", "paraphrase", "perturbed"];

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    /// <summary>Loads, resolves and validates a catalog. Never throws for content problems; check <see cref="QuestionCatalog.Errors"/>.</summary>
    public static QuestionCatalog Load(Workspace ws, string path)
    {
        var full = ws.Resolve(path);
        if (!File.Exists(full)) throw new BenchException($"Catalog file not found: {full}");

        var bytes = File.ReadAllBytes(full);
        List<CatalogQuestion> questions;
        string? parseError = null;
        try
        {
            questions = JsonSerializer.Deserialize<List<CatalogQuestion>>(bytes, Json.Indented) ?? [];
        }
        catch (JsonException ex)
        {
            questions = [];
            parseError = $"JSON error: {ex.Message}";
        }

        var catalog = new QuestionCatalog
        {
            FullPath = full,
            RelativePath = ws.MakeRelative(full),
            Sha256 = Json.Sha256(bytes),
            Questions = questions,
        };
        if (parseError != null) catalog.Errors.Add(parseError);
        else
        {
            foreach (var q in questions) Resolve(q, catalog.Errors);
            Validate(ws, catalog);
        }
        return catalog;
    }

    /// <summary>Stable option id derived from the option text.</summary>
    public static string OptionId(string text, ISet<string> used)
    {
        var id = NonSlug().Replace(text.ToLowerInvariant(), "-").Trim('-');
        if (id.Length == 0) id = "opt";
        if (id.Length > 32) id = id[..32].TrimEnd('-');
        var candidate = id;
        for (int n = 2; used.Contains(candidate); n++) candidate = $"{id}-{n}";
        used.Add(candidate);
        return candidate;
    }

    /// <summary>Turns every <see cref="VariantSpec"/> into a complete <see cref="QuestionVariant"/>.</summary>
    public static void Resolve(CatalogQuestion q, List<string> errors)
    {
        q.Variants = [];
        var b = q.Base;
        // option ids from the base list, so the same text has the same id in every variant
        var baseIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in b.Options) if (!baseIds.ContainsKey(o)) baseIds[o] = OptionId(o, used);

        foreach (var s in q.VariantSpecs)
        {
            var name = $"{q.Id}/{s.Id}";
            var options = s.Options ?? b.Options;
            var format = s.Format ?? (options.Count > 0 ? "choice" : "free");
            var answer = s.Answer ?? b.Answer;
            var v = new QuestionVariant
            {
                Id = s.Id,
                Kind = s.Kind ?? "original",
                Type = format,
                Question = s.Question ?? b.Question,
                Image = s.Image ?? b.Image,
                FixedOrder = s.FixedOrder ?? b.FixedOrder,
                Tier = s.Tier ?? q.Tier,
                Note = s.Note ?? (s.Question == null && s.Answer == null ? b.Note : null),
            };

            if (string.Equals(format, "choice", StringComparison.OrdinalIgnoreCase))
            {
                var shown = options.ToList();
                if (s.OptionCount is int n)
                {
                    if (n < 2 || n > shown.Count) errors.Add($"{name}: optionCount {n} must be between 2 and {shown.Count}.");
                    else
                    {
                        // correct answer plus the first n-1 distractors, in list order
                        var distractors = shown.Where(o => !SameAnswer(o, answer)).Take(n - 1).ToHashSet();
                        shown = shown.Where(o => SameAnswer(o, answer) || distractors.Contains(o)).ToList();
                    }
                }
                var ids = new HashSet<string>(baseIds.Values, StringComparer.Ordinal);
                v.Options = shown.Select(t => new AnswerOption { Id = baseIds.TryGetValue(t, out var id) ? id : OptionId(t, ids), Text = t }).ToList();
                var correct = v.Options.Where(o => SameAnswer(o.Text, answer)).ToList();
                if (correct.Count == 1) v.CorrectOptionId = correct[0].Id;
                else if (!string.IsNullOrWhiteSpace(answer))
                    errors.Add($"{name}: the answer \"{answer}\" must be exactly one of the options.");
            }
            else
            {
                v.AnswerType = s.AnswerType ?? b.AnswerType ?? "text";
                var acc = new List<string>();
                if (!string.IsNullOrWhiteSpace(answer)) acc.Add(answer);
                acc.AddRange(s.AcceptedAnswers ?? (s.Answer == null ? b.AcceptedAnswers : []));
                v.AcceptedAnswers = acc.Distinct().ToList();
            }
            q.Variants.Add(v);
        }
    }

    private static bool SameAnswer(string option, string answer) =>
        string.Equals(option.Trim(), answer.Trim(), StringComparison.OrdinalIgnoreCase);

    public static void Validate(Workspace ws, QuestionCatalog catalog)
    {
        var errors = catalog.Errors;
        if (catalog.Questions.Count == 0) errors.Add("Catalog contains no questions.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in catalog.Questions)
        {
            var qName = string.IsNullOrWhiteSpace(q.Id) ? "(question without id)" : q.Id;
            if (string.IsNullOrWhiteSpace(q.Id)) errors.Add("A question has no 'id'.");
            else if (!ids.Add(q.Id)) errors.Add($"{qName}: duplicate question id.");
            if (q.Tier is < 1 || q.Tier > MaxTier) errors.Add($"{qName}: tier must be between 1 and {MaxTier}.");
            if (string.IsNullOrWhiteSpace(q.Base.Question)) errors.Add($"{qName}: base question is empty.");
            if (string.IsNullOrWhiteSpace(q.Base.Answer)) errors.Add($"{qName}: base answer is empty.");
            if (q.Base.Options.Count > 0 && q.Base.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != q.Base.Options.Count)
                errors.Add($"{qName}: duplicate options in the base answer list.");
            if (q.Base.Options.Count > 0 && !q.Base.Options.Any(o => SameAnswer(o, q.Base.Answer)))
                errors.Add($"{qName}: the base answer \"{q.Base.Answer}\" is not in the base options.");
            if (q.VariantSpecs.Count == 0) errors.Add($"{qName}: no variants (nothing would be asked).");

            var variantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in q.VariantSpecs)
            {
                var vName = $"{qName}/{(string.IsNullOrWhiteSpace(s.Id) ? "?" : s.Id)}";
                if (string.IsNullOrWhiteSpace(s.Id)) errors.Add($"{vName}: variant without 'id'.");
                else if (!variantIds.Add(s.Id)) errors.Add($"{vName}: duplicate variant id.");
                if (s.Kind != null && !Kinds.Contains(s.Kind)) errors.Add($"{vName}: kind must be one of {string.Join(", ", Kinds)}.");
                if (s.Format is not (null or "choice" or "free")) errors.Add($"{vName}: format must be 'choice' or 'free'.");
                if (s.Tier is < 1 || s.Tier > MaxTier) errors.Add($"{vName}: tier must be between 1 and {MaxTier}.");
                if (s.Kind is "altered" && s.Question == null) errors.Add($"{vName}: an altered variant needs its own 'question'.");
            }

            foreach (var v in q.Variants)
            {
                var vName = $"{qName}/{v.Id}";
                if (string.IsNullOrWhiteSpace(v.Question)) errors.Add($"{vName}: empty question text.");
                if (v.IsChoice)
                {
                    if (v.Options.Count < 2) errors.Add($"{vName}: a choice variant needs at least 2 options (base list or own list).");
                    if (v.Options.Count > MaxOptions) errors.Add($"{vName}: at most {MaxOptions} options are supported.");
                }
                else
                {
                    if (v.AcceptedAnswers.Count == 0) errors.Add($"{vName}: a free variant needs an answer.");
                    if (v.AnswerType is not ("text" or "number")) errors.Add($"{vName}: answerType must be 'text' or 'number'.");
                    if (v.IsNumber)
                        foreach (var a in v.AcceptedAnswers)
                            if (AnswerNormalizer.ParseSingleNumber(a) is null)
                                errors.Add($"{vName}: accepted answer '{a}' is not a number.");
                }
                if (!string.IsNullOrWhiteSpace(v.Image) && !File.Exists(ws.Resolve(v.Image)))
                    errors.Add($"{vName}: image file not found: {v.Image}");
            }
        }
    }

    /// <summary>Loads a catalog and throws a readable exception when it is invalid.</summary>
    public static QuestionCatalog LoadValid(Workspace ws, string path)
    {
        var c = Load(ws, path);
        if (!c.IsValid)
            throw new BenchException($"Catalog '{c.RelativePath}' is invalid:\n  " + string.Join("\n  ", c.Errors));
        return c;
    }
}
