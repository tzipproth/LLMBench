using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMBench.Core.Runs;

/// <summary>Appends JSONL lines and flushes after every line, so an aborted run stays readable.</summary>
public sealed class ResultWriter : IDisposable
{
    private readonly StreamWriter _w;

    public ResultWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _w = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    public void Write<T>(T record)
    {
        _w.WriteLine(JsonSerializer.Serialize(record, Json.Compact));
        _w.Flush();
    }

    public void Dispose() => _w.Dispose();
}

/// <summary>Content of a results.jsonl file.</summary>
public sealed class RunResults
{
    public RunHeader? Header { get; set; }
    public List<ItemRecord> Items { get; } = [];
    public RunSummary? Summary { get; set; }
    public List<string> ReadErrors { get; } = [];

    public static RunResults Load(string path)
    {
        var r = new RunResults();
        if (!File.Exists(path)) return r;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        int lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var type = JsonNode.Parse(line)?["type"]?.GetValue<string>();
                switch (type)
                {
                    case "run": r.Header = JsonSerializer.Deserialize<RunHeader>(line, Json.Compact); break;
                    case "item": r.Items.Add(JsonSerializer.Deserialize<ItemRecord>(line, Json.Compact)!); break;
                    case "summary": r.Summary = JsonSerializer.Deserialize<RunSummary>(line, Json.Compact); break;
                    default: r.ReadErrors.Add($"line {lineNo}: unknown type '{type}'"); break;
                }
            }
            catch (JsonException ex)
            {
                r.ReadErrors.Add($"line {lineNo}: {ex.Message}");
            }
        }
        return r;
    }
}
