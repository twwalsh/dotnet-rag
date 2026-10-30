namespace SupportRag.Core.Generation;

/// <summary>What the model produced, reduced to what the pipeline needs. Parsing it is a pure function so it has tests.</summary>
public sealed record Answer(string Text, string? Form, long InputTokens, long OutputTokens, long CacheReadTokens)
{
    /// <summary>
    /// Folds a response's content blocks into text plus at most one recognised form. An unknown tool, an
    /// unknown form name or a malformed input shows no form; the answer text still stands, which is the
    /// same forgiveness the Worker's stream parser has.
    /// </summary>
    public static (string Text, string? Form) Fold(IEnumerable<ContentPart> parts)
    {
        var text = new System.Text.StringBuilder();
        string? form = null;
        foreach (var part in parts)
        {
            if (part.Text is not null)
            {
                text.Append(part.Text);
            }
            else if (part.ToolName == Prompts.ShowFormTool && form is null && part.ToolForm is { } f && Prompts.Forms.Contains(f))
            {
                form = f;
            }
        }

        return (text.ToString().Trim(), form);
    }
}

/// <summary>A content block with only the fields the fold cares about.</summary>
public sealed record ContentPart(string? Text = null, string? ToolName = null, string? ToolForm = null);
