using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using SupportRag.Core.Options;
using SupportRag.Core.Retrieval;
using SupportRag.Core.Telemetry;

namespace SupportRag.Core.Generation;

/// <summary>
/// One Messages call: the system prompt (cached), the show_form tool, and a user turn carrying the
/// passages and the question in the same &lt;passages&gt; markup the Worker sends, so the model sees the
/// same thing from both builds.
/// </summary>
public sealed class Answerer(AnthropicClient client, IOptions<RagOptions> options)
{
    private readonly RagOptions _o = options.Value;

    private static readonly Tool ShowForm = new()
    {
        Name = Prompts.ShowFormTool,
        Description = "Show the visitor one of the site's self-service forms below your answer. They enter their own email in it; you never see it.",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["form"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = Prompts.Forms }),
            },
            Required = ["form"],
        },
    };

    public async Task<Answer> AnswerAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct)
    {
        using var activity = Instrumentation.Source.StartActivity("rag.generate");
        activity?.SetTag("gen_ai.request.model", _o.Model);

        var parameters = new MessageCreateParams
        {
            Model = _o.Model,
            MaxTokens = _o.MaxAnswerTokens,
            System = new List<TextBlockParam> { new(Prompts.System) { CacheControl = new CacheControlEphemeral() } },
            Tools = [ShowForm],
            Messages = [new UserMessageParam(UserMessage(question, passages))],
        };

        var message = await client.Messages.Create(parameters, cancellationToken: ct);
        var stop = message.StopReason?.Raw();
        activity?.SetTag("gen_ai.response.finish_reason", stop);
        if (stop == "max_tokens")
        {
            // Thinking plus answer plus tool call outran the cap: the visible answer is cut and the form is lost.
            Instrumentation.Truncations.Add(1);
            activity?.SetTag("rag.truncated", true);
        }

        if (Environment.GetEnvironmentVariable("RAG_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[rag debug] stop={stop} blocks={message.Content.Count} usage={message.Usage}");
        }

        var parts = message.Content.Select(block =>
            block.TryPickText(out var text) ? new ContentPart(Text: text.Text)
            : block.TryPickToolUse(out var tool) ? new ContentPart(ToolName: tool.Name, ToolForm: FormOf(tool.Input))
            : new ContentPart());
        var (text, form) = Answer.Fold(parts);

        var usage = message.Usage;
        Instrumentation.InputTokens.Add(usage.InputTokens);
        Instrumentation.OutputTokens.Add(usage.OutputTokens);
        activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        activity?.SetTag("rag.form", form);

        return new Answer(text, form, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0);
    }

    private static string? FormOf(IReadOnlyDictionary<string, JsonElement> input) =>
        input.TryGetValue("form", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

    /// <summary>The Worker's exact layout: numbered passages with title and url, then the question.</summary>
    internal static string UserMessage(string question, IReadOnlyList<Passage> passages)
    {
        var sb = new StringBuilder("<passages>\n");
        for (var i = 0; i < passages.Count; i++)
        {
            var p = passages[i];
            sb.Append("<passage n=\"").Append(i + 1).Append("\" title=\"").Append(p.Title.Replace('"', '\'')).Append('"');
            if (!string.IsNullOrEmpty(p.Url))
            {
                sb.Append(" url=\"").Append(p.Url).Append('"');
            }

            sb.Append(">\n").Append(p.Text).Append("\n</passage>\n");
        }

        sb.Append("</passages>\n\nVisitor's question: ").Append(question);
        return sb.ToString();
    }
}
