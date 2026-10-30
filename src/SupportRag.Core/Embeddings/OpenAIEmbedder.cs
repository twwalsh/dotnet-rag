using Microsoft.Extensions.AI;

namespace SupportRag.Core.Embeddings;

/// <summary>Azure OpenAI (or any <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>), through the Microsoft.Extensions.AI abstraction.</summary>
public sealed class OpenAIEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator, string modelId) : IEmbedder
{
    public string ModelId { get; } = modelId;

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var embedding = await generator.GenerateAsync(query, cancellationToken: ct);
        return embedding.Vector.ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        if (passages.Count == 0)
        {
            return [];
        }

        var embeddings = await generator.GenerateAsync(passages, cancellationToken: ct);
        return embeddings.Select(e => e.Vector.ToArray()).ToList();
    }
}
