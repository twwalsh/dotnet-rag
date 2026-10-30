namespace SupportRag.Core.Embeddings;

/// <summary>
/// The one thing the pipeline needs from an embedding provider, with the asymmetry some models have made
/// explicit: bge-style models want an instruction prefix on the query and none on the passages; OpenAI-style
/// models want neither. Keeping the distinction here means the retriever and the ingestor cannot get it wrong.
/// </summary>
public interface IEmbedder
{
    /// <summary>Identifies the model so stored vectors can be recognised as stale when it changes.</summary>
    string ModelId { get; }

    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);

    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct);
}
