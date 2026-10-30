namespace SupportRag.Core.Ingest;

/// <summary>A chunk as it arrives from the corpus file.</summary>
public sealed record IncomingChunk(string Id, string Source, string Title, string Url, string Text, string ContentHash);

/// <summary>What the database already holds, reduced to what the diff needs.</summary>
public sealed record StoredChunk(string Id, string ContentHash, string? EmbeddingModel);

/// <summary>
/// The decision of what to do on an ingest, with no I/O in it: which incoming chunks need (re-)embedding,
/// which stored ones are gone. Idempotent by construction: running it twice on the same file and model
/// yields an empty plan the second time.
/// </summary>
public sealed record IngestPlan(IReadOnlyList<IncomingChunk> ToEmbed, IReadOnlyList<string> ToDelete, int Unchanged)
{
    public static IngestPlan Compute(IReadOnlyList<IncomingChunk> incoming, IReadOnlyList<StoredChunk> stored, string embeddingModel)
    {
        var storedById = stored.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var incomingIds = new HashSet<string>(incoming.Select(c => c.Id), StringComparer.Ordinal);

        var toEmbed = new List<IncomingChunk>();
        var unchanged = 0;
        foreach (var chunk in incoming)
        {
            if (storedById.TryGetValue(chunk.Id, out var s)
                && s.ContentHash == chunk.ContentHash
                && string.Equals(s.EmbeddingModel, embeddingModel, StringComparison.Ordinal))
            {
                unchanged++;
            }
            else
            {
                toEmbed.Add(chunk);
            }
        }

        var toDelete = stored.Select(s => s.Id).Where(id => !incomingIds.Contains(id)).ToList();
        return new IngestPlan(toEmbed, toDelete, unchanged);
    }
}
