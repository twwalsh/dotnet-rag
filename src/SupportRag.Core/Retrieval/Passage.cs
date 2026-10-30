namespace SupportRag.Core.Retrieval;

/// <param name="Score">Cosine similarity between the question and the passage (1 = identical), for source display and for the eval.</param>
public sealed record Passage(string Id, string Source, string Title, string Url, string Text, double Score);
