using SupportRag.Core.Ingest;

namespace SupportRag.Tests;

public class IngestPlanTests
{
    private static IncomingChunk In(string id, string hash) => new(id, "faq", "t", "", "text", hash);

    [Fact]
    public void A_fresh_table_embeds_everything()
    {
        var plan = IngestPlan.Compute([In("a", "h1"), In("b", "h2")], [], "m1");
        Assert.Equal(2, plan.ToEmbed.Count);
        Assert.Empty(plan.ToDelete);
        Assert.Equal(0, plan.Unchanged);
    }

    [Fact]
    public void The_same_file_and_model_is_a_no_op()
    {
        var plan = IngestPlan.Compute([In("a", "h1")], [new StoredChunk("a", "h1", "m1")], "m1");
        Assert.Empty(plan.ToEmbed);
        Assert.Empty(plan.ToDelete);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void Changed_text_re_embeds_only_that_chunk()
    {
        var plan = IngestPlan.Compute([In("a", "h1"), In("b", "h2-new")], [new StoredChunk("a", "h1", "m1"), new StoredChunk("b", "h2", "m1")], "m1");
        Assert.Equal(["b"], plan.ToEmbed.Select(c => c.Id));
    }

    [Fact]
    public void A_new_embedding_model_re_embeds_everything()
    {
        var plan = IngestPlan.Compute([In("a", "h1")], [new StoredChunk("a", "h1", "m1")], "m2");
        Assert.Single(plan.ToEmbed);
    }

    [Fact]
    public void A_chunk_missing_from_the_file_is_deleted()
    {
        var plan = IngestPlan.Compute([In("a", "h1")], [new StoredChunk("a", "h1", "m1"), new StoredChunk("gone", "h9", "m1")], "m1");
        Assert.Equal(["gone"], plan.ToDelete);
    }

    [Fact]
    public async Task The_corpus_file_reads_cleanly_with_unique_ids()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "chunks.jsonl");
        var chunks = await CorpusIngestor.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(219, chunks.Count);
        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        Assert.All(chunks, c => Assert.Matches("^[0-9a-f]{64}$", c.ContentHash));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
