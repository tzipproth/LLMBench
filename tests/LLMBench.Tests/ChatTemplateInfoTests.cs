using LLMBench.Core.Llama;
using Xunit;

namespace LLMBench.Tests;

public class ChatTemplateInfoTests
{
    // excerpt of the Qwen 3.8 template (Unsloth)
    private const string Qwen = """
        {%- if enable_thinking is undefined or enable_thinking is true %}
            {%- set resolved_reasoning_effort = reasoning_effort|default('xhigh') %}
            {%- if resolved_reasoning_effort == 'high' %}
                {%- set resolved_reasoning_effort = 'xhigh' %}
            {%- endif %}
            {%- if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}
                {{- raise_exception('Unexpected reasoning effort') }}
            {%- endif %}
        {%- endif %}
        {%- if add_generation_prompt %}
            {{- '<|im_start|>assistant\n' }}
            {%- if enable_thinking is defined and enable_thinking is false %}
                {{- '<think>\n\n</think>\n\n' }}
            {%- else %}
                {{- '<think>\n' }}
            {%- endif %}
        {%- endif %}
        """;

    [Fact]
    public void Qwen_template_switches_are_recognised()
    {
        var i = ChatTemplateInfo.Analyze(Qwen);
        Assert.True(i.EnableThinking);
        Assert.True(i.ThinkingDefaultOn);
        Assert.True(i.ReasoningEffort);
        Assert.Equal(["low", "medium", "high", "xhigh"], i.EffortValues);
        Assert.Equal("xhigh", i.EffortDefault);
        Assert.Contains("<think>", i.Markers);
        Assert.Equal("enable_thinking (default on) | reasoning_effort: low, medium, high, xhigh (default xhigh) | <think>", i.Summary());
    }

    [Fact]
    public void Template_without_switches()
    {
        var i = ChatTemplateInfo.Analyze("{% for m in messages %}{{ m.content }}{% endfor %}");
        Assert.False(i.EnableThinking);
        Assert.False(i.ReasoningEffort);
        Assert.Equal("no enable_thinking | no reasoning_effort", i.Summary());
    }

    [Fact]
    public void Thinking_off_unless_requested()
    {
        var i = ChatTemplateInfo.Analyze("{% if enable_thinking is defined and enable_thinking %}<think>{% endif %}");
        Assert.False(i.ThinkingDefaultOn);
    }

    [Fact]
    public void Granite_style_default_is_recognised()
    {
        var i = ChatTemplateInfo.Analyze("""
            {%- set enable_thinking = enable_thinking if enable_thinking is defined else True %}
            {%- if reasoning_effort is defined and reasoning_effort is not none %}{%- set low_effort = reasoning_effort == "low" %}{%- endif %}
            {%- if enable_thinking %}<think>{%- else %}<think></think>{%- endif %}
            """);
        Assert.True(i.ThinkingDefaultOn);
        Assert.Equal(["low"], i.EffortValues);
    }

    [Fact]
    public void Missing_template() => Assert.Equal("no chat template in /props", ChatTemplateInfo.Analyze(null).Summary());
}
