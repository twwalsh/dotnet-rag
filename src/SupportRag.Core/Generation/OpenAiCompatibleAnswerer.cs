using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportRag.Core.Retrieval;
using SupportRag.Core.Telemetry;

namespace SupportRag.Core.Generation;

/// <summary>
/// One chat-completions call to an OpenAI-compatible endpoint. Workers AI exposes one for every text model it
/// hosts, so this is how the open-weight generators (Llama, Qwen, gpt-oss) answer from the same passages and
/// score on the same eval as Claude; a local llama-server speaks the same protocol. The form is a tagged last
/// line rather than a tool call: these models do support tools, but on this prompt every one probed answered
/// with the tool call alone and no text (2026-11-04), and a second round trip would double the cost being
/// measured. The Databricks build made the same choice for the same reason.
/// </summary>
public sealed class OpenAiCompatibleAnswerer(HttpClient http, Uri completions, string model, string provider, int maxTokens) : IAnswerer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ModelId => model;

    public string Provider => provider;

    public async Task<Answer> AnswerAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct)
    {
        using var activity = Instrumentation.Source.StartActivity($"chat {model}", ActivityKind.Client);
        activity?.SetTag(Instrumentation.GenAi.OperationName, "chat");
        activity?.SetTag(Instrumentation.GenAi.ProviderName, provider);
        activity?.SetTag(Instrumentation.GenAi.System, provider);
        activity?.SetTag(Instrumentation.GenAi.RequestModel, model);
        activity?.SetTag(Instrumentation.GenAi.RequestMaxTokens, maxTokens);
        activity?.SetTag("rag.passages", passages.Count);
        var clock = Stopwatch.StartNew();

        var request = new ChatRequest(model, maxTokens,
        [
            new ChatMessage("system", Prompts.SystemTaggedForm),
            new ChatMessage("user", ClaudeAnswerer.UserMessage(question, passages)),
        ]);

        ChatResponse body;
        try
        {
            using var response = await http.PostAsJsonAsync(completions, request, Json, ct);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, ct)
                   ?? throw new InvalidOperationException($"{provider} returned an empty body.");
        }
        catch (Exception e)
        {
            activity?.SetTag(Instrumentation.GenAi.ErrorType, e.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            Instrumentation.GenAiOperationSeconds.Record(clock.Elapsed.TotalSeconds, Instrumentation.ChatTags(provider, model, e.GetType().Name));
            throw;
        }

        var choice = body.Choices?.FirstOrDefault() ?? throw new InvalidOperationException($"{provider} returned no choices.");
        var finish = choice.FinishReason;
        activity?.SetTag(Instrumentation.GenAi.ResponseId, body.Id);
        activity?.SetTag(Instrumentation.GenAi.ResponseModel, body.Model ?? model);
        activity?.SetTag(Instrumentation.GenAi.FinishReasons, finish is null ? Array.Empty<string>() : new[] { finish });
        activity?.SetTag("gen_ai.response.finish_reason", finish);
        if (finish == "length")
        {
            Instrumentation.Truncations.Add(1);
            activity?.SetTag("rag.truncated", true);
        }

        if (Environment.GetEnvironmentVariable("RAG_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[rag debug] finish={finish} usage={body.Usage} content={choice.Message?.Content}");
        }

        var (text, form) = Answer.ParseTagged(choice.Message?.Content ?? "");

        var usage = body.Usage ?? new ChatUsage(0, 0, null);
        Instrumentation.InputTokens.Add(usage.PromptTokens);
        Instrumentation.OutputTokens.Add(usage.CompletionTokens);
        Instrumentation.GenAiTokenUsage.Record(usage.PromptTokens, Instrumentation.ChatTags(provider, model, null, "input"));
        Instrumentation.GenAiTokenUsage.Record(usage.CompletionTokens, Instrumentation.ChatTags(provider, model, null, "output"));
        Instrumentation.GenAiOperationSeconds.Record(clock.Elapsed.TotalSeconds, Instrumentation.ChatTags(provider, model, null));
        activity?.SetTag(Instrumentation.GenAi.InputTokens, usage.PromptTokens);
        activity?.SetTag(Instrumentation.GenAi.OutputTokens, usage.CompletionTokens);
        activity?.SetTag("rag.form", form);
        if (usage.Neurons is { } neurons)
        {
            activity?.SetTag("cloudflare.neurons", neurons); // Workers AI's billing unit, when the endpoint reports it
        }

        return new Answer(text, form, usage.PromptTokens, usage.CompletionTokens, 0);
    }

    // The wire shape, only the fields used. Reading is case-insensitive (Web defaults); the snake_case names are spelled out.
    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(string? Id, string? Model, List<ChatChoice>? Choices, ChatUsage? Usage);

    private sealed record ChatChoice([property: JsonPropertyName("finish_reason")] string? FinishReason, ChatChoiceMessage? Message);

    private sealed record ChatChoiceMessage(string? Content);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
        [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
        [property: JsonPropertyName("neurons")] double? Neurons);
}
