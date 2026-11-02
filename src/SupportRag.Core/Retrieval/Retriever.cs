using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SupportRag.Core.Data;
using SupportRag.Core.Embeddings;
using SupportRag.Core.Generation;
using SupportRag.Core.Options;
using SupportRag.Core.Telemetry;

namespace SupportRag.Core.Retrieval;

/// <summary>
/// Hybrid retrieval in Postgres: the question's vector against pgvector (cosine) and its words against a
/// generated tsvector (Postgres full-text search), fused by reciprocal rank. The Databricks build turns
/// hybrid on with a flag; here it is two queries and a merge, which is also where you can see what each
/// half contributes. A licensing-shaped question additionally pins the best FAQ entries, as in both other
/// builds, so the three systems retrieve under the same rules.
/// </summary>
public sealed class Retriever(RagDbContext db, IEmbedder embedder, IOptions<RagOptions> options)
{
    private readonly RagOptions _o = options.Value;

    public async Task<IReadOnlyList<Passage>> RetrieveAsync(string question, CancellationToken ct)
    {
        using var activity = Instrumentation.Source.StartActivity("rag.retrieve");
        activity?.SetTag("rag.retrieval.kind", "hybrid");
        activity?.SetTag("rag.embedding.model", embedder.ModelId);

        var query = new Vector(await EmbedQueryAsync(question, ct));

        var general = await HybridAsync(question, query, _o.TopK, faqOnly: false, ct);
        var pinned = Prompts.IsLicensingQuestion(question)
            ? await HybridAsync(question, query, _o.FaqPinned, faqOnly: true, ct)
            : [];

        // Pinned first, then the general results, de-duplicated, capped: the order the model reads them in.
        var ordered = new List<string>(pinned.Count + general.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pinned.Concat(general))
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        ordered = ordered.Take(_o.Passages).ToList();
        if (ordered.Count == 0)
        {
            activity?.SetTag("rag.passages", 0);
            return [];
        }

        // One round trip for the rows and their cosine similarity to the question (the display score).
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => ordered.Contains(c.Id) && c.Embedding != null)
            .Select(c => new { c.Id, c.Source, c.Title, c.Url, c.Text, Distance = c.Embedding!.CosineDistance(query) })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var passages = ordered
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Select(r => new Passage(r.Id, r.Source, r.Title, r.Url, r.Text, Math.Round(1 - r.Distance, 4)))
            .ToList();

        var top = passages.Count > 0 ? passages.Max(p => p.Score) : 0;
        activity?.SetTag("rag.passages", passages.Count);
        activity?.SetTag("rag.pinned", pinned.Count);
        activity?.SetTag("rag.top_score", top);
        Instrumentation.RetrievalTopScore.Record(top);
        return passages;
    }

    /// <summary>The embedding call as its own GenAI span: operation, provider and model, so a backend can cost it and time it apart from the chat call.</summary>
    private async Task<float[]> EmbedQueryAsync(string question, CancellationToken ct)
    {
        var (provider, model) = ProviderAndModel(embedder.ModelId);
        using var activity = Instrumentation.Source.StartActivity($"embeddings {model}", System.Diagnostics.ActivityKind.Client);
        activity?.SetTag(Instrumentation.GenAi.OperationName, "embeddings");
        activity?.SetTag(Instrumentation.GenAi.ProviderName, provider);
        activity?.SetTag(Instrumentation.GenAi.System, provider);
        activity?.SetTag(Instrumentation.GenAi.RequestModel, model);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await embedder.EmbedQueryAsync(question, ct);
        }
        catch (Exception e)
        {
            activity?.SetTag(Instrumentation.GenAi.ErrorType, e.GetType().Name);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, e.Message);
            throw;
        }
        finally
        {
            Instrumentation.GenAiOperationSeconds.Record(clock.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(Instrumentation.GenAi.OperationName, "embeddings"),
                new KeyValuePair<string, object?>(Instrumentation.GenAi.ProviderName, provider),
                new KeyValuePair<string, object?>(Instrumentation.GenAi.RequestModel, model));
        }
    }

    /// <summary>"azure-openai/text-embedding-3-small" → ("azure.ai.openai", "text-embedding-3-small"); the ids are the embedders' own.</summary>
    internal static (string Provider, string Model) ProviderAndModel(string embedderModelId)
    {
        var slash = embedderModelId.IndexOf('/');
        if (slash < 0)
        {
            return ("cloudflare", embedderModelId); // Workers AI ids are "@cf/baai/bge-base-en-v1.5"-shaped only after the first slash; see below
        }

        var prefix = embedderModelId[..slash];
        var rest = embedderModelId[(slash + 1)..];
        return prefix switch
        {
            "azure-openai" => ("azure.ai.openai", rest),
            "openai" => ("openai", rest),
            "@cf" => ("cloudflare", embedderModelId),
            _ => (prefix, rest),
        };
    }

    private async Task<IReadOnlyList<string>> HybridAsync(string question, Vector query, int take, bool faqOnly, CancellationToken ct)
    {
        if (take <= 0)
        {
            return [];
        }

        var candidates = take * 2; // each half over-fetches so the fusion has something to fuse
        var scope = db.Chunks.AsNoTracking().Where(c => c.Embedding != null);
        if (faqOnly)
        {
            scope = scope.Where(c => c.Source == "faq");
        }

        var byVector = await scope
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Take(candidates)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // EF.Functions.* only translates inside the expression tree; hoisting it to a local makes EF try to
        // run it on the client and throw, so the tsquery is spelled out twice.
        var byKeyword = await scope
            .Where(c => c.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("english", question)))
            .OrderByDescending(c => c.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("english", question)))
            .Take(candidates)
            .Select(c => c.Id)
            .ToListAsync(ct);

        return ReciprocalRankFusion.Fuse([byVector, byKeyword]).Take(take).ToList();
    }
}
