using System.Diagnostics;
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
        // The chat span, named and attributed per the OTel GenAI conventions: "{operation} {model}".
        using var activity = Instrumentation.Source.StartActivity($"chat {_o.Model}", ActivityKind.Client); // GenAI conventions: inference calls are CLIENT spans
        activity?.SetTag(Instrumentation.GenAi.OperationName, "chat");
        activity?.SetTag(Instrumentation.GenAi.ProviderName, "anthropic");
        activity?.SetTag(Instrumentation.GenAi.System, "anthropic");
        activity?.SetTag(Instrumentation.GenAi.RequestModel, _o.Model);
        activity?.SetTag(Instrumentation.GenAi.RequestMaxTokens, _o.MaxAnswerTokens);
        activity?.SetTag("rag.passages", passages.Count);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var parameters = new MessageCreateParams
        {
            Model = _o.Model,
            MaxTokens = _o.MaxAnswerTokens,
            System = new List<TextBlockParam> { new(Prompts.System) { CacheControl = new CacheControlEphemeral() } },
            Tools = [ShowForm],
            Messages = [new UserMessageParam(UserMessage(question, passages))],
        };

        Message message;
        try
        {
            message = await client.Messages.Create(parameters, cancellationToken: ct);
        }
        catch (Exception e)
        {
            activity?.SetTag(Instrumentation.GenAi.ErrorType, e.GetType().Name);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, e.Message);
            Instrumentation.GenAiOperationSeconds.Record(clock.Elapsed.TotalSeconds, ChatTags(e.GetType().Name));
            throw;
        }

        var stop = message.StopReason?.Raw();
        activity?.SetTag(Instrumentation.GenAi.ResponseId, message.ID);
        activity?.SetTag(Instrumentation.GenAi.ResponseModel, message.Model.Raw());
        activity?.SetTag(Instrumentation.GenAi.FinishReasons, stop is null ? Array.Empty<string>() : new[] { stop });
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
        var cacheRead = usage.CacheReadInputTokens ?? 0;
        Instrumentation.InputTokens.Add(usage.InputTokens);
        Instrumentation.OutputTokens.Add(usage.OutputTokens);
        Instrumentation.CacheReadTokens.Add(cacheRead);
        Instrumentation.GenAiTokenUsage.Record(usage.InputTokens, ChatTags(null, "input"));
        Instrumentation.GenAiTokenUsage.Record(usage.OutputTokens, ChatTags(null, "output"));
        Instrumentation.GenAiOperationSeconds.Record(clock.Elapsed.TotalSeconds, ChatTags(null));
        activity?.SetTag(Instrumentation.GenAi.InputTokens, usage.InputTokens);
        activity?.SetTag(Instrumentation.GenAi.OutputTokens, usage.OutputTokens);
        activity?.SetTag(Instrumentation.GenAi.CacheReadInputTokens, cacheRead);
        activity?.SetTag("rag.form", form);

        return new Answer(text, form, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0);
    }

    /// <summary>The metric attributes for the chat call; the token type and the error type are added when they apply.</summary>
    private TagList ChatTags(string? errorType, string? tokenType = null)
    {
        var tags = new TagList
        {
            { Instrumentation.GenAi.OperationName, "chat" },
            { Instrumentation.GenAi.ProviderName, "anthropic" },
            { Instrumentation.GenAi.RequestModel, _o.Model },
        };
        if (tokenType is not null)
        {
            tags.Add(Instrumentation.GenAi.TokenType, tokenType);
        }

        if (errorType is not null)
        {
            tags.Add(Instrumentation.GenAi.ErrorType, errorType);
        }

        return tags;
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
