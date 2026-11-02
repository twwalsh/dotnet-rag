using System.Diagnostics;
using Microsoft.Extensions.Options;
using SupportRag.Core.Generation;
using SupportRag.Core.Options;
using SupportRag.Core.Retrieval;
using SupportRag.Core.Telemetry;

namespace SupportRag.Core.Pipeline;

/// <summary>Retrieve, then answer only from what was retrieved. The same two steps as ask.py and support.ts.</summary>
public sealed class SupportAssistant(Retriever retriever, Answerer answerer, IOptions<RagOptions> options)
{
    public const string AgentName = "support-assistant";

    private readonly RagOptions _o = options.Value;

    public async Task<AskResult> AskAsync(string question, CancellationToken ct)
    {
        // The agent-level span: one per question, parent of the embedding, retrieval and chat spans.
        using var activity = Instrumentation.Source.StartActivity($"invoke_agent {AgentName}");
        activity?.SetTag(Instrumentation.GenAi.OperationName, "invoke_agent");
        activity?.SetTag(Instrumentation.GenAi.AgentName, AgentName);
        activity?.SetTag(Instrumentation.GenAi.RequestModel, _o.Model);
        activity?.SetTag("rag.question.chars", question.Length);
        var clock = Stopwatch.StartNew();
        Instrumentation.Questions.Add(1);

        var passages = await retriever.RetrieveAsync(question, ct);
        activity?.SetTag("rag.passages", passages.Count);

        // Nothing retrieved → no model call. With an empty passage list the model answers from general
        // knowledge however firmly the prompt forbids it (observed 2026-10-23 against a still-empty index in
        // the Databricks build: it invented a control and offered a form). All three builds now refuse here.
        if (passages.Count == 0)
        {
            Instrumentation.Refusals.Add(1);
            activity?.SetTag("rag.refused", true);
            activity?.SetTag("rag.outcome", "refused");
            Instrumentation.AnswerSeconds.Record(clock.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("rag.outcome", "refused"));
            return new AskResult(question, Prompts.Nothing, null, null, [], [], new Usage(0, 0, 0, clock.Elapsed.TotalSeconds));
        }

        activity?.SetTag("rag.refused", false);
        var answer = await answerer.AnswerAsync(question, passages, ct);
        if (answer.Form is { } form)
        {
            Instrumentation.FormsOffered.Add(1, new KeyValuePair<string, object?>("form", form));
        }

        var sources = passages
            .Where(p => !string.IsNullOrEmpty(p.Url) && p.Score >= _o.SourceMinScore)
            .DistinctBy(p => p.Url, StringComparer.Ordinal)
            .Take(_o.Sources)
            .Select(p => new SourceLink(ShortTitle(p.Title), p.Url))
            .ToList();

        // "handoff": the answer points the visitor at a person (the contact form) or a self-service form,
        // which is the fail-closed path a support assistant should take when it is not the right tool.
        var outcome = answer.Form is not null ? "form" : answer.Text.Contains("/contact/", StringComparison.Ordinal) ? "handoff" : "answered";
        activity?.SetTag("rag.outcome", outcome);
        activity?.SetTag("rag.sources", sources.Count);
        activity?.SetTag("rag.form", answer.Form);

        Instrumentation.AnswerSeconds.Record(clock.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("rag.outcome", outcome));
        return new AskResult(question, answer.Text, answer.Form, _o.Model, passages, sources,
            new Usage(answer.InputTokens, answer.OutputTokens, answer.CacheReadTokens, clock.Elapsed.TotalSeconds));
    }

    /// <summary>"Manual > Trammel > SLOW attack" reads better under an answer as "Trammel > SLOW attack".</summary>
    internal static string ShortTitle(string title)
    {
        var parts = title.Split(" > ");
        return parts.Length <= 2 ? title : string.Join(" > ", parts[^2..]);
    }
}
