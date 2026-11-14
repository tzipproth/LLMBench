using System.Globalization;

namespace LLMBench.Cli;

/// <summary>Tiny argument parser: positional arguments plus --key value / --flag options.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = ["shuffle", "no-shuffle", "help", "all", "verbose", "no-question", "dry-run"];

    public List<string> Positional { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var list = argv.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                var key = s[2..];
                string? value = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { value = key[(eq + 1)..]; key = key[..eq]; }
                else if (!Flags.Contains(key) && i + 1 < list.Count) value = list[++i];
                a.Options[key] = value;
            }
            else a.Positional.Add(s);
        }
        return a;
    }

    public bool Has(string key) => Options.ContainsKey(key);
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public string Pos(int i, string what) =>
        i < Positional.Count ? Positional[i] : throw new UsageException($"Missing argument: {what}");

    public int? GetInt(string key) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
}

public sealed class UsageException(string message) : Exception(message);
