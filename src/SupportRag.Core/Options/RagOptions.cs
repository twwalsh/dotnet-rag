using System.ComponentModel.DataAnnotations;

namespace SupportRag.Core.Options;

/// <summary>
/// Everything the assistant needs that is not a secret. Bound from the <c>Rag</c> configuration section
/// and validated at startup, so a bad deployment fails on boot rather than on the first question.
/// Secrets come from the environment: <c>ANTHROPIC_API_KEY</c>, <c>AZURE_OPENAI_API_KEY</c> (or a managed
/// identity), <c>CLOUDFLARE_API_TOKEN</c>, <c>ConnectionStrings__Rag</c>, <c>RAG_ADMIN_KEY</c>.
/// </summary>
public sealed class RagOptions
{
    public const string Section = "Rag";

    /// <summary>The corpus, one chunk per line: id, source, title, url, text. Exported by the site's chunker.</summary>
    [Required]
    public string ChunksPath { get; set; } = "data/chunks.jsonl";

    /// <summary>The generator. The Cloudflare build uses the same model, so answers are comparable.</summary>
    [Required]
    public string Model { get; set; } = "claude-haiku-5-5";

    /// <summary>
    /// The Worker caps answers at 700. Claude 5 models think adaptively by default and the thinking counts
    /// against max_tokens, so at 700 a longer-than-usual think truncates the answer and drops the tool call
    /// (first eval run, 2026-10-30: two cases cut at exactly 700 output tokens, 28/30). 2000 leaves room.
    /// </summary>
    [Range(100, 8000)]
    public int MaxAnswerTokens { get; set; } = 2000;

    /// <summary>The retrieval limits, identical across the three builds: top 6, plus 3 pinned FAQ entries, 8 passages to the model.</summary>
    [Range(1, 50)]
    public int TopK { get; set; } = 6;

    [Range(0, 10)]
    public int FaqPinned { get; set; } = 3;

    [Range(1, 50)]
    public int Passages { get; set; } = 8;

    /// <summary>How many sources are listed under an answer.</summary>
    [Range(0, 10)]
    public int Sources { get; set; } = 3;

    /// <summary>
    /// A passage is cited as a source only when its cosine similarity reached this. Below it the passage was
    /// merely the nearest thing in an index with nothing on the question. The 0.65 was measured on bge-base;
    /// other embedding models score differently, so it is configuration, not a constant.
    /// </summary>
    [Range(0, 1)]
    public double SourceMinScore { get; set; } = 0.65;

    [Range(1, 2000)]
    public int QuestionMaxChars { get; set; } = 500;

    public EmbedderKind Embedder { get; set; } = EmbedderKind.AzureOpenAI;

    public AzureOpenAIOptions AzureOpenAI { get; set; } = new();

    public WorkersAiOptions WorkersAi { get; set; } = new();

    public OpenAIOptions OpenAI { get; set; } = new();

    /// <summary>Hour (UTC) at which the background service re-ingests the corpus. The ingest is idempotent, so a missed run costs nothing.</summary>
    [Range(0, 23)]
    public int IngestHourUtc { get; set; } = 3;

    /// <summary>Ingest on startup when the table is empty, so a fresh database answers questions without an operator step.</summary>
    public bool IngestOnStartupIfEmpty { get; set; } = true;
}

public enum EmbedderKind
{
    /// <summary>Azure OpenAI embeddings (text-embedding-3-small by default). The production choice for the Azure deployment.</summary>
    AzureOpenAI,

    /// <summary>Cloudflare Workers AI, bge-base-en-v1.5: the model the live site uses, so retrieval can be compared like for like.</summary>
    WorkersAi,

    /// <summary>OpenAI's own API with the same embedding model the Azure deployment serves (OPENAI_API_KEY). The stand-in until an Azure subscription exists.</summary>
    OpenAI,
}

public sealed class AzureOpenAIOptions
{
    /// <summary>https://&lt;resource&gt;.openai.azure.com/ — required when Embedder is AzureOpenAI.</summary>
    public string? Endpoint { get; set; }

    public string Deployment { get; set; } = "text-embedding-3-small";

    /// <summary>When true, authenticate with DefaultAzureCredential (managed identity in Azure, az login locally) instead of AZURE_OPENAI_API_KEY.</summary>
    public bool UseManagedIdentity { get; set; }
}

public sealed class WorkersAiOptions
{
    /// <summary>The Cloudflare account id — required when Embedder is WorkersAi. The token comes from CLOUDFLARE_API_TOKEN.</summary>
    public string? AccountId { get; set; }

    public string Model { get; set; } = "@cf/baai/bge-base-en-v1.5";
}

public sealed class OpenAIOptions
{
    public string Model { get; set; } = "text-embedding-3-small";
}
