using LLMBench.Core.Runs;
using LLMBench.Core.Stats;
using Xunit;

namespace LLMBench.Tests;

public class RunTimingTests
{
    [Fact]
    public void Figures_are_computed_from_the_summary()
    {
        var s = new RunSummary { Done = 10, CompletionTokens = 5000, RequestSeconds = 50 };
        Assert.Equal(5.0, RunTiming.SecondsPerAnswer(s));
        Assert.Equal(500.0, RunTiming.TokensPerAnswer(s));
        Assert.Equal(100.0, RunTiming.TokensPerSecond(s));
    }

    [Fact]
    public void Empty_run_has_no_figures()
    {
        var s = new RunSummary();
        Assert.Null(RunTiming.SecondsPerAnswer(s));
        Assert.Null(RunTiming.TokensPerAnswer(s));
        Assert.Null(RunTiming.TokensPerSecond(s));
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(128, "2.1 min")]
    [InlineData(7500, "2 h 05 min")]
    public void Duration_is_readable(double seconds, string expected) => Assert.Equal(expected, RunTiming.Duration(seconds));
}
