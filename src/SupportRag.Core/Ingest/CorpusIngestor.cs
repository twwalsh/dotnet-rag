using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using SupportRag.Core.Data;
using SupportRag.Core.Embeddings;
using SupportRag.Core.Options;
using SupportRag.Core.Telemetry;

namespace SupportRag.Core.Ingest;

public sealed record IngestReport(int Seen, int Embedded, int Unchanged, int Deleted, string EmbeddingModel, TimeSpan Duration);

/// <summary>
/// Reads the corpus file, diffs it against the table, embeds only what changed, deletes what disappeared.
/// The same contract as the site's indexing script (stable ids, content hashes, nothing re-embedded for
/// nothing), written against Postgres instead of Vectorize.
/// </summary>
public sealed class CorpusIngestor(RagDbContext db, IEmbedder embedder, IOptions<RagOptions> options, ILogger<CorpusIngestor> log)
{
    private const int EmbedBatch = 32;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IngestReport> IngestAsync(CancellationToken ct)
    {
        using var activity = Instrumentation.Source.StartActivity("rag.ingest");
        var started = DateTimeOffset.UtcNow;
        var path = options.Value.ChunksPath;

        var incoming = await ReadAsync(path, ct);
        var stored = await db.Chunks.AsNoTracking()
            .Select(c => new StoredChunk(c.Id, c.ContentHash, c.EmbeddingModel))
            .ToListAsync(ct);
        var plan = IngestPlan.Compute(incoming, stored, embedder.ModelId);
        log.LogInformation("Ingest {Path}: {Seen} chunks, {ToEmbed} to embed, {Unchanged} unchanged, {ToDelete} to delete (model {Model})",
            path, incoming.Count, plan.ToEmbed.Count, plan.Unchanged, plan.ToDelete.Count, embedder.ModelId);

        if (plan.ToDelete.Count > 0)
        {
            await db.Chunks.Where(c => plan.ToDelete.Contains(c.Id)).ExecuteDeleteAsync(ct);
        }

        var embedded = 0;
        foreach (var batch in plan.ToEmbed.Chunk(EmbedBatch))
        {
            var vectors = await embedder.EmbedPassagesAsync(batch.Select(c => c.Text).ToList(), ct);
            var ids = batch.Select(c => c.Id).ToList();
            var existing = await db.Chunks.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            for (var i = 0; i < batch.Length; i++)
            {
                var c = batch[i];
                if (existing.TryGetValue(c.Id, out var row))
                {
                    row.Source = c.Source;
                    row.Title = c.Title;
                    row.Url = c.Url;
                    row.Text = c.Text;
                    row.ContentHash = c.ContentHash;
                }
                else
                {
                    row = new Chunk { Id = c.Id, Source = c.Source, Title = c.Title, Url = c.Url, Text = c.Text, ContentHash = c.ContentHash };
                    db.Chunks.Add(row);
                }

                row.Embedding = new Vector(vectors[i]);
                row.EmbeddingModel = embedder.ModelId;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            embedded += batch.Length;
        }

        var report = new IngestReport(incoming.Count, embedded, plan.Unchanged, plan.ToDelete.Count, embedder.ModelId, DateTimeOffset.UtcNow - started);
        activity?.SetTag("rag.ingest.embedded", embedded);
        activity?.SetTag("rag.ingest.deleted", plan.ToDelete.Count);
        log.LogInformation("Ingest done in {Seconds:F1}s: {Embedded} embedded, {Deleted} deleted", report.Duration.TotalSeconds, embedded, plan.ToDelete.Count);
        return report;
    }

    public async Task<bool> IsEmptyAsync(CancellationToken ct) => !await db.Chunks.AnyAsync(ct);

    internal static async Task<IReadOnlyList<IncomingChunk>> ReadAsync(string path, CancellationToken ct)
    {
        var chunks = new List<IncomingChunk>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var raw = JsonSerializer.Deserialize<RawChunk>(line, Json) ?? throw new InvalidDataException($"Bad line in {path}");
            if (string.IsNullOrWhiteSpace(raw.Id) || string.IsNullOrWhiteSpace(raw.Text))
            {
                throw new InvalidDataException($"Chunk without id or text in {path}");
            }

            if (!seen.Add(raw.Id))
            {
                throw new InvalidDataException($"Duplicate chunk id {raw.Id} in {path}");
            }

            chunks.Add(new IncomingChunk(raw.Id, raw.Source ?? "", raw.Title ?? "", raw.Url ?? "", raw.Text, Hash(raw)));
        }

        return chunks;
    }

    internal static string Hash(RawChunk c)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\u001f', c.Source, c.Title, c.Url, c.Text));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal sealed record RawChunk(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("text")] string Text);
}
