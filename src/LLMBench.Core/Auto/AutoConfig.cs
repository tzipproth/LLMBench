namespace LLMBench.Core.Auto;

/// <summary>
/// Configuration for unattended runs over several models (llmbench auto): how to start llama-server for each model
/// and which extra run options each model needs. Every model gets its own server process, one after another.
/// </summary>
public sealed class AutoConfig
{
    /// <summary>Path of the llama-server executable.</summary>
    public string ServerExe { get; set; } = "";

    /// <summary>URL the benchmark uses to reach the started server (must match --host/--port in the args).</summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:8081";

    /// <summary>Maximum time from start until /health reports ok (model loading included).</summary>
    public int ReadyTimeoutSeconds { get; set; } = 900;

    /// <summary>Maximum time to wait until a stopped server has released the port.</summary>
    public int StopTimeoutSeconds { get; set; } = 60;

    public List<AutoModel> Models { get; set; } = [];

    public static AutoConfig Load(string path)
    {
        if (!File.Exists(path)) throw new BenchException($"Auto config not found: {path}");
        AutoConfig? cfg;
        try { cfg = Json.Deserialize<AutoConfig>(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException ex) { throw new BenchException($"{path}: invalid JSON - {ex.Message}"); }
        if (cfg == null) throw new BenchException($"{path}: empty config.");
        var problems = cfg.Check();
        if (problems.Count > 0) throw new BenchException($"{path}:\n  " + string.Join("\n  ", problems));
        return cfg;
    }

    public List<string> Check()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerExe)) p.Add("serverExe is missing.");
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out _)) p.Add("serverUrl is not a valid URL.");
        if (ReadyTimeoutSeconds < 10) p.Add("readyTimeoutSeconds must be at least 10.");
        if (Models.Count == 0) p.Add("models is empty.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Models)
        {
            if (string.IsNullOrWhiteSpace(m.Alias)) { p.Add("a model has no alias."); continue; }
            if (!seen.Add(m.Alias)) p.Add($"alias '{m.Alias}' occurs twice.");
            if (string.IsNullOrWhiteSpace(m.Args)) p.Add($"{m.Alias}: args are missing.");
            else
            {
                try
                {
                    var args = CommandLine.Split(m.Args);
                    var i = args.FindIndex(x => x == "--alias" || x == "-a");
                    if (i < 0 || i + 1 >= args.Count || args[i + 1] != m.Alias)
                        p.Add($"{m.Alias}: args do not contain the alias (expected '--alias {m.Alias}'), the run would refuse the model name.");
                }
                catch (FormatException ex) { p.Add($"{m.Alias}: args - {ex.Message}"); }
            }
            if (m.Bench != null) { try { CommandLine.Split(m.Bench); } catch (FormatException ex) { p.Add($"{m.Alias}: bench - {ex.Message}"); } }
        }
        return p;
    }
}

public sealed class AutoModel
{
    /// <summary>Model name as llama-server reports it (--alias); also the second part of the run name.</summary>
    public string Alias { get; set; } = "";

    /// <summary>llama-server arguments as on a shell command line (quotes allowed, line continuations ignored).</summary>
    public string Args { get; set; } = "";

    /// <summary>llama-server executable for this model only (optional, overrides the global serverExe).</summary>
    public string? ServerExe { get; set; }

    /// <summary>Extra run options for this model, e.g. "--thinking default" (optional).</summary>
    public string? Bench { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Free text, ignored by the program.</summary>
    public string? Note { get; set; }
}
