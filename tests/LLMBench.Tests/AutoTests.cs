using LLMBench.Core.Auto;
using Xunit;

namespace LLMBench.Tests;

public class AutoTests
{
    [Fact]
    public void Splits_like_a_shell()
    {
        var a = CommandLine.Split("-m  /models/x.gguf --alias muse \\\n  --chat-template-kwargs '{\"reasoning_strength\":\"high\"}' --note \"a \\\"b\\\" c\" x\\ y");
        Assert.Equal(["-m", "/models/x.gguf", "--alias", "muse", "--chat-template-kwargs", "{\"reasoning_strength\":\"high\"}", "--note", "a \"b\" c", "x y"], a);
    }

    [Fact]
    public void Unclosed_quote_is_an_error() => Assert.Throws<FormatException>(() => CommandLine.Split("--x 'abc"));

    [Fact]
    public void Config_check_finds_problems()
    {
        var cfg = new AutoConfig
        {
            ServerExe = "/llama-server",
            Models =
            [
                new() { Alias = "a", Args = "-m a.gguf --alias a" },
                new() { Alias = "a", Args = "-m b.gguf --alias a" },
                new() { Alias = "c", Args = "-m c.gguf --alias other" },
                new() { Alias = "d", Args = "-m 'd.gguf --alias d" },
            ],
        };
        var p = cfg.Check();
        Assert.Contains(p, x => x.Contains("'a' occurs twice"));
        Assert.Contains(p, x => x.StartsWith("c: args do not contain the alias"));
        Assert.Contains(p, x => x.StartsWith("d: args - unclosed single quote"));
    }

    [Fact]
    public void Generated_config_is_valid()
    {
        var path = Path.Combine(FindRepo(), "auto", "models.json");
        var cfg = AutoConfig.Load(path);
        Assert.Equal(cfg.Models.Count, cfg.Models.Select(m => m.Alias).Distinct().Count());
        foreach (var alias in new[] { "gemma4-26b-a4b", "qwen3.6-35b-a3b" })
            Assert.Contains(LLMBench.Core.Stats.LeaderboardEntry.ExpertsInRamNote, cfg.Models.Single(m => m.Alias == alias).Bench);
        var muse = cfg.Models.Single(m => m.Alias == "muse-glimmer-30b");
        Assert.Contains("{\"reasoning_strength\":\"high\"}", CommandLine.Split(muse.Args));
        Assert.Equal("/home/thomas/llama/llama-server", cfg.Models.Single(m => m.Alias == "phi-4-14b").ServerExe);
        Assert.Null(cfg.Models.Single(m => m.Alias == "gemma4-12b").ServerExe);
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "LLMBench.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
