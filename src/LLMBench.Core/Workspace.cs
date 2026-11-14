using LLMBench.Core.Runs;

namespace LLMBench.Core;

/// <summary>
/// The data folder: catalogs/, prompts/, runs/, images/.
/// All paths stored in run definitions are relative to <see cref="Root"/>.
/// </summary>
public sealed class Workspace
{
    public string Root { get; }
    public string CatalogsDir => Path.Combine(Root, "catalogs");
    public string PromptsDir => Path.Combine(Root, "prompts");
    public string RunsDir => Path.Combine(Root, "runs");

    public RunStore Runs { get; }

    /// <summary>Parameters every new run starts with (written by "Save as default for new runs").</summary>
    public string RunDefaultsPath => Path.Combine(Root, "run-defaults.json");

    public RunDefinition LoadRunDefaults()
    {
        if (!File.Exists(RunDefaultsPath)) return new RunDefinition();
        try { return Json.Deserialize<RunDefinition>(File.ReadAllText(RunDefaultsPath))?.AsTemplate() ?? new RunDefinition(); }
        catch (System.Text.Json.JsonException ex) { throw new BenchException($"{RunDefaultsPath}: {ex.Message}"); }
    }

    public void SaveRunDefaults(RunDefinition def) =>
        File.WriteAllText(RunDefaultsPath, Json.Serialize(def.AsTemplate()));

    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        if (!IsWorkspace(Root))
            throw new BenchException($"'{Root}' is not an LLMBench workspace (needs folders 'catalogs' and 'prompts').");
        Runs = new RunStore(this);
    }

    public static bool IsWorkspace(string dir) =>
        Directory.Exists(Path.Combine(dir, "catalogs")) && Directory.Exists(Path.Combine(dir, "prompts"));

    /// <summary>
    /// Finds the workspace: explicit path, LLMBENCH_HOME, then walking up from the current
    /// directory and from the executable's directory.
    /// </summary>
    public static Workspace Find(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return new Workspace(explicitPath);

        var env = Environment.GetEnvironmentVariable("LLMBENCH_HOME");
        if (!string.IsNullOrWhiteSpace(env) && IsWorkspace(env)) return new Workspace(env);

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (IsWorkspace(dir.FullName)) return new Workspace(dir.FullName);
        }
        throw new BenchException("No LLMBench workspace found. Run from the project folder, set LLMBENCH_HOME or pass --workspace <dir>.");
    }

    public string Resolve(string relativeOrAbsolute) =>
        Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolute) ? relativeOrAbsolute : Path.Combine(Root, relativeOrAbsolute));

    public string MakeRelative(string path)
    {
        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(Root, full);
        return rel.StartsWith("..", StringComparison.Ordinal) ? full : rel.Replace('\\', '/');
    }

    public IReadOnlyList<string> ListCatalogs() => ListFiles(CatalogsDir, "*.json");

    public IReadOnlyList<string> ListPrompts() => ListFiles(PromptsDir, "*.txt");

    private List<string> ListFiles(string dir, string pattern) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(MakeRelative).ToList()
            : [];
}
