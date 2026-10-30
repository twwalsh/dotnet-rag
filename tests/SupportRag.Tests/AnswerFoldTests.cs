using SupportRag.Core.Generation;

namespace SupportRag.Tests;

public class AnswerFoldTests
{
    [Fact]
    public void Text_blocks_concatenate_and_a_known_form_is_kept()
    {
        var (text, form) = Answer.Fold([new ContentPart(Text: "Use the form below."), new ContentPart(ToolName: "show_form", ToolForm: "resend-license")]);
        Assert.Equal("Use the form below.", text);
        Assert.Equal("resend-license", form);
    }

    [Fact]
    public void An_unknown_form_name_shows_nothing_and_keeps_the_answer()
    {
        var (text, form) = Answer.Fold([new ContentPart(Text: "Answer."), new ContentPart(ToolName: "show_form", ToolForm: "refund-me")]);
        Assert.Equal("Answer.", text);
        Assert.Null(form);
    }

    [Fact]
    public void An_unknown_tool_is_ignored()
    {
        var (_, form) = Answer.Fold([new ContentPart(ToolName: "send_email", ToolForm: "resend-license")]);
        Assert.Null(form);
    }

    [Fact]
    public void Only_the_first_form_counts()
    {
        var (_, form) = Answer.Fold([new ContentPart(ToolName: "show_form", ToolForm: "resend-download"), new ContentPart(ToolName: "show_form", ToolForm: "resend-license")]);
        Assert.Equal("resend-download", form);
    }

    [Fact]
    public void Whitespace_is_trimmed()
    {
        var (text, _) = Answer.Fold([new ContentPart(Text: "  hi \n")]);
        Assert.Equal("hi", text);
    }
}
