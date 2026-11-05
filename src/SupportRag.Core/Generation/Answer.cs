using System.Text.RegularExpressions;

namespace SupportRag.Core.Generation;

/// <summary>What the model produced, reduced to what the pipeline needs. Parsing it is a pure function so it has tests.</summary>
public sealed partial record Answer(string Text, string? Form, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens = 0)
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

    /// <summary>
    /// The tagged-line form of the same contract, for a model reached without tool calling: the answer ends
    /// with a final line <c>FORM: name</c>. The line is removed whether or not the name is known, and an
    /// unknown name shows no form. The pattern is the Databricks build's (src/ask.py), so the two builds read
    /// the same answer the same way.
    /// </summary>
    public static (string Text, string? Form) ParseTagged(string text)
    {
        var m = TaggedForm().Match(text);
        if (!m.Success)
        {
            return (text.Trim(), null);
        }

        var name = m.Groups[1].Value;
        return (text[..m.Index].Trim(), Prompts.Forms.Contains(name) ? name : null);
    }

    [GeneratedRegex(@"\n?\s*FORM:\s*([a-z-]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TaggedForm();
}

/// <summary>A content block with only the fields the fold cares about.</summary>
public sealed record ContentPart(string? Text = null, string? ToolName = null, string? ToolForm = null);
