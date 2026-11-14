using System.Text.RegularExpressions;

namespace LLMBench.Core.Runs;

public enum RunState { Draft, Running, Completed, Aborted, Incomplete }

public sealed class RunInfo
{
    public required string Name { get; init; }
    public required RunDefinition Definition { get; init; }
    public required RunState State { get; init; }
    public RunHeader? Header { get; init; }
    public RunSummary? Summary { get; init; }
    public int ItemsDone { get; init; }
    public DateTime? StartedAt => Header?.StartedAt;
}

/// <summary>
/// Run folders: runs/&lt;name&gt;/run.json (parameters) and runs/&lt;name&gt;/results.jsonl (results).
/// A run with results is never started again; "restart" copies its parameters to &lt;name&gt;_01, _02, ...
/// </summary>
public sealed partial class RunStore(Workspace ws)
{
    public const string DefinitionFileName = "run.json";
    public const string ResultsFileName = "results.jsonl";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,79}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"_(\d{2,})$")]
    private static partial Regex RestartSuffix();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name ?? "");

    public string RunDir(string name) => Path.Combine(ws.RunsDir, name);
    public string DefinitionPath(string name) => Path.Combine(RunDir(name), DefinitionFileName);
    public string ResultsPath(string name) => Path.Combine(RunDir(name), ResultsFileName);

    public bool Exists(string name) => File.Exists(DefinitionPath(name));
    public bool HasResults(string name) => File.Exists(ResultsPath(name));

    public RunDefinition Load(string name)
    {
        var path = DefinitionPath(name);
        if (!File.Exists(path)) throw new BenchException($"Run '{name}' does not exist ({path}).");
        var def = Json.Deserialize<RunDefinition>(File.ReadAllText(path))
                  ?? throw new BenchException($"Cannot read {path}.");
        def.Name = name; // folder name wins
        return def;
    }

    public void Save(RunDefinition def)
    {
        if (!IsValidName(def.Name)) throw new BenchException($"Invalid run name '{def.Name}'.");
        if (HasResults(def.Name)) throw new BenchException($"Run '{def.Name}' already has results; its parameters cannot be changed. Use restart or duplicate.");
        def.CreatedAt ??= DateTime.Now;
        Directory.CreateDirectory(RunDir(def.Name));
        File.WriteAllText(DefinitionPath(def.Name), Json.Serialize(def));
    }

    public IReadOnlyList<string> ListNames() =>
        Directory.Exists(ws.RunsDir)
            ? Directory.GetDirectories(ws.RunsDir)
                .Where(d => File.Exists(Path.Combine(d, DefinitionFileName)))
                .Select(d => Path.GetFileName(d)!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public RunInfo GetInfo(string name, bool isRunning = false)
    {
        var def = Load(name);
        if (!HasResults(name))
            return new RunInfo { Name = name, Definition = def, State = isRunning ? RunState.Running : RunState.Draft };
        var res = RunResults.Load(ResultsPath(name));
        var state = isRunning ? RunState.Running
            : res.Summary == null ? RunState.Incomplete
            : res.Summary.Aborted ? RunState.Aborted
            : RunState.Completed;
        return new RunInfo
        {
            Name = name, Definition = def, State = state,
            Header = res.Header, Summary = res.Summary, ItemsDone = res.Items.Count,
        };
    }

    public List<RunInfo> ListRuns(string? runningName = null)
    {
        var list = new List<RunInfo>();
        foreach (var n in ListNames())
        {
            try { list.Add(GetInfo(n, n == runningName)); }
            catch (Exception) { /* unreadable run folder: ignore in listing */ }
        }
        return list;
    }

    /// <summary>Next free restart name: "x" → "x_01", "x_01" → "x_02" (if free).</summary>
    public string NextRestartName(string name)
    {
        var baseName = RestartSuffix().Replace(name, "");
        for (int i = 1; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i:D2}";
            if (!Directory.Exists(RunDir(candidate))) return candidate;
        }
        throw new BenchException($"No free restart name for '{name}'.");
    }

    /// <summary>Creates a new draft run with the same parameters.</summary>
    public RunDefinition CreateRestart(string name)
    {
        var def = Load(name).Clone();
        def.RestartOf = name;
        def.Name = NextRestartName(name);
        def.CreatedAt = DateTime.Now;
        Save(def);
        return def;
    }

    /// <summary>Copies parameters to a new draft run with the given name.</summary>
    public RunDefinition Duplicate(string name, string newName)
    {
        if (Directory.Exists(RunDir(newName))) throw new BenchException($"Run '{newName}' already exists.");
        var def = Load(name).Clone();
        def.Name = newName;
        def.RestartOf = null;
        def.CreatedAt = DateTime.Now;
        Save(def);
        return def;
    }

    /// <summary>Suggests "name_copy", "name_copy2", ... that does not exist yet.</summary>
    public string SuggestNewName(string baseName)
    {
        if (!Directory.Exists(RunDir(baseName))) return baseName;
        for (int i = 2; ; i++)
            if (!Directory.Exists(RunDir($"{baseName}{i}"))) return $"{baseName}{i}";
    }
}
