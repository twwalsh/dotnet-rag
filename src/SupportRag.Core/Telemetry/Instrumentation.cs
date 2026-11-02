using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SupportRag.Core.Telemetry;

/// <summary>
/// One ActivitySource and one Meter for the whole pipeline; the host decides where they go (OTLP, console, nowhere).
/// Spans carry the OpenTelemetry GenAI semantic conventions (<c>gen_ai.*</c>) so an AI-observability backend
/// recognises the model call, the embedding call and the agent-level request without a vendor SDK; the
/// <c>rag.*</c> counters and the two GenAI client histograms are the metrics side of the same story.
/// </summary>
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
    public static readonly Counter<long> CacheReadTokens = Meter.CreateCounter<long>("rag.tokens.cache_read", description: "Model input tokens served from the prompt cache");
    public static readonly Histogram<double> AnswerSeconds = Meter.CreateHistogram<double>("rag.answer.duration", unit: "s", description: "End-to-end time per question");
    public static readonly Histogram<double> RetrievalTopScore = Meter.CreateHistogram<double>("rag.retrieval.top_score", description: "Best cosine similarity among the retrieved passages (0 when nothing was retrieved)");

    /// <summary>OTel GenAI client metric: tokens per call, split by <c>gen_ai.token.type</c> (input / output).</summary>
    public static readonly Histogram<long> GenAiTokenUsage = Meter.CreateHistogram<long>("gen_ai.client.token.usage", unit: "{token}", description: "Number of input and output tokens used per GenAI operation");

    /// <summary>OTel GenAI client metric: wall time of one model or embedding call.</summary>
    public static readonly Histogram<double> GenAiOperationSeconds = Meter.CreateHistogram<double>("gen_ai.client.operation.duration", unit: "s", description: "GenAI operation duration");

    /// <summary>The attribute set the GenAI conventions put on spans and metrics; one place so the names cannot drift.</summary>
    public static class GenAi
    {
        public const string OperationName = "gen_ai.operation.name";
        public const string ProviderName = "gen_ai.provider.name";
        public const string System = "gen_ai.system"; // the pre-1.37 name of provider.name; emitted too for backends that still key on it
        public const string RequestModel = "gen_ai.request.model";
        public const string RequestMaxTokens = "gen_ai.request.max_tokens";
        public const string ResponseModel = "gen_ai.response.model";
        public const string ResponseId = "gen_ai.response.id";
        public const string FinishReasons = "gen_ai.response.finish_reasons";
        public const string InputTokens = "gen_ai.usage.input_tokens";
        public const string OutputTokens = "gen_ai.usage.output_tokens";
        public const string CacheReadInputTokens = "gen_ai.usage.cache_read.input_tokens";
        public const string TokenType = "gen_ai.token.type";
        public const string AgentName = "gen_ai.agent.name";
        public const string ErrorType = "error.type";
    }
}
