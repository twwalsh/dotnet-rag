using SupportRag.Core.Retrieval;

namespace SupportRag.Core.Pipeline;

public sealed record AskResult(
    string Question,
    string Answer,
    string? Form,
    string? Model,
    IReadOnlyList<Passage> Passages,
    IReadOnlyList<SourceLink> Sources,
    Usage Usage);

public sealed record SourceLink(string Title, string Url);

/// <summary>The model call's token counts and the end-to-end time. Cache reads and writes are zero for a provider without a prompt cache.</summary>
public sealed record Usage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, double Seconds);
