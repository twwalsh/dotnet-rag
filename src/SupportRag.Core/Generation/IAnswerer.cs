using SupportRag.Core.Retrieval;

namespace SupportRag.Core.Generation;

/// <summary>
/// The generator behind the pipeline: passages and a question in, an answer and at most one form out. Two
/// implementations, chosen by configuration: Claude through the official SDK (<see cref="ClaudeAnswerer"/>),
/// and any OpenAI-compatible chat endpoint, which is how the open-weight models on Workers AI are reached
/// (<see cref="OpenAiCompatibleAnswerer"/>). Retrieval, prompt and eval are the same either way, so a score or
/// a cost difference between two generators is a model difference and nothing else.
/// </summary>
public interface IAnswerer
{
    /// <summary>The model id as its provider names it; what the API reports and what the eval records.</summary>
    string ModelId { get; }

    /// <summary>The <c>gen_ai.provider.name</c> value for spans and metrics.</summary>
    string Provider { get; }

    Task<Answer> AnswerAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct);
}
