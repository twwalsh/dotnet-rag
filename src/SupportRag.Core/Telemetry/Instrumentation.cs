using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SupportRag.Core.Telemetry;

/// <summary>One ActivitySource and one Meter for the whole pipeline; the host decides where they go (OTLP, console, nowhere).</summary>
public static class Instrumentation
{
    public const string Name = "SupportRag";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Questions = Meter.CreateCounter<long>("rag.questions", description: "Questions asked");
    public static readonly Counter<long> Refusals = Meter.CreateCounter<long>("rag.refusals", description: "Questions refused without a model call (nothing retrieved)");
    public static readonly Counter<long> FormsOffered = Meter.CreateCounter<long>("rag.forms_offered", description: "Self-service forms offered, by form");
    public static readonly Counter<long> Truncations = Meter.CreateCounter<long>("rag.truncations", description: "Answers cut off by max_tokens");
    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("rag.tokens.input", description: "Model input tokens");
    public static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("rag.tokens.output", description: "Model output tokens");
    public static readonly Histogram<double> AnswerSeconds = Meter.CreateHistogram<double>("rag.answer.duration", unit: "s", description: "End-to-end time per question");
}
