using SupportRag.Core.Generation;

namespace SupportRag.Tests;

/// <summary>The tagged-line form contract used with generators reached without tool calling.</summary>
public class TaggedFormTests
{
    [Fact]
    public void A_known_form_on_the_last_line_is_taken_and_the_line_removed()
    {
        var (text, form) = Answer.ParseTagged("Check your spam folder first, then use the form below.\nFORM: resend-license\n");
        Assert.Equal("Check your spam folder first, then use the form below.", text);
        Assert.Equal("resend-license", form);
    }

    [Fact]
    public void An_unknown_form_name_is_removed_and_shows_nothing()
    {
        var (text, form) = Answer.ParseTagged("Answer.\nFORM: refund-me");
        Assert.Equal("Answer.", text);
        Assert.Null(form);
    }

    [Fact]
    public void No_tag_means_no_form_and_the_answer_untouched()
    {
        var (text, form) = Answer.ParseTagged("  The FORM: of a thing is not a tag mid-sentence. \n");
        Assert.Equal("The FORM: of a thing is not a tag mid-sentence.", text);
        Assert.Null(form);
    }

    [Fact]
    public void A_tag_earlier_than_the_last_line_does_not_count()
    {
        var (text, form) = Answer.ParseTagged("FORM: resend-license\nAnd then some more text.");
        Assert.Null(form);
        Assert.Equal("FORM: resend-license\nAnd then some more text.", text);
    }

    [Fact]
    public void The_tagged_prompt_differs_from_the_tool_prompt_only_in_the_form_paragraph()
    {
        // The derivation is by replacement, so it is only right while both sentences are still in the original.
        Assert.Contains(Prompts.ToolSentence, Prompts.System, StringComparison.Ordinal);
        Assert.Contains(Prompts.ToolRule, Prompts.System, StringComparison.Ordinal);
        Assert.DoesNotContain("show_form", Prompts.SystemTaggedForm, StringComparison.Ordinal);
        Assert.Contains("FORM: <name>", Prompts.SystemTaggedForm, StringComparison.Ordinal);
        Assert.Equal(
            Prompts.System.Replace(Prompts.ToolSentence, "", StringComparison.Ordinal).Replace(Prompts.ToolRule, "", StringComparison.Ordinal),
            Prompts.SystemTaggedForm.Replace(Prompts.TaggedSentence, "", StringComparison.Ordinal).Replace(Prompts.TaggedRule, "", StringComparison.Ordinal));
    }
}
