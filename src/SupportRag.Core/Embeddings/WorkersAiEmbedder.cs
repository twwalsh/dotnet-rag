using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SupportRag.Core.Embeddings;

/// <summary>
/// Cloudflare Workers AI over its REST API. The live site embeds with this exact model inside a Worker, so
/// running the .NET build against it isolates the storage and retrieval differences from the embedding ones.
/// </summary>
public sealed class WorkersAiEmbedder(HttpClient http, string accountId, string model) : IEmbedder
{
    // bge's retrieval instruction: queries get it, passages do not (the same two lines live in the Worker).
    private const string QueryPrefix = "Represent this sentence for searching relevant passages: ";
    private const int BatchSize = 100; // the API's per-call cap

    private readonly string _model = model;

    public string ModelId => _model;

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var vectors = await RunAsync(new List<string> { QueryPrefix + query }, ct);
        return vectors[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        var all = new List<float[]>(passages.Count);
        for (var i = 0; i < passages.Count; i += BatchSize)
        {
            all.AddRange(await RunAsync(passages.Skip(i).Take(BatchSize).ToList(), ct));
        }

        return all;
    }

    private async Task<IReadOnlyList<float[]>> RunAsync(List<string> texts, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            new Uri($"accounts/{accountId}/ai/run/{_model}", UriKind.Relative),
            new { text = texts },
            ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RunResponse>(ct)
                   ?? throw new InvalidOperationException("Workers AI returned an empty body.");
        if (!body.Success || body.Result?.Data is null || body.Result.Data.Count != texts.Count)
        {
            throw new InvalidOperationException($"Workers AI embedding failed: {string.Join("; ", body.Errors?.Select(e => e.Message) ?? [])}");
        }

        return body.Result.Data;
    }

    private sealed record RunResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("result")] RunResult? Result,
        [property: JsonPropertyName("errors")] List<ApiError>? Errors);

    private sealed record RunResult([property: JsonPropertyName("data")] List<float[]> Data);

    private sealed record ApiError([property: JsonPropertyName("message")] string Message);
}
