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
    private readonly RagOptions _o = options.Value;

    public async Task<AskResult> AskAsync(string question, CancellationToken ct)
    {
        using var activity = Instrumentation.Source.StartActivity("rag.ask");
        var clock = Stopwatch.StartNew();
        Instrumentation.Questions.Add(1);

        var passages = await retriever.RetrieveAsync(question, ct);

        // Nothing retrieved → no model call. With an empty passage list the model answers from general
        // knowledge however firmly the prompt forbids it (observed 2026-10-23 against a still-empty index in
        // the Databricks build: it invented a control and offered a form). All three builds now refuse here.
        if (passages.Count == 0)
        {
            Instrumentation.Refusals.Add(1);
            activity?.SetTag("rag.refused", true);
            return new AskResult(question, Prompts.Nothing, null, null, [], [], new Usage(0, 0, 0, clock.Elapsed.TotalSeconds));
        }

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

        Instrumentation.AnswerSeconds.Record(clock.Elapsed.TotalSeconds);
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
