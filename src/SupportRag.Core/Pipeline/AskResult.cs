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

public sealed record Usage(long InputTokens, long OutputTokens, long CacheReadTokens, double Seconds);
