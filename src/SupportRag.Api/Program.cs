using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SupportRag.Core;
using SupportRag.Core.Data;
using SupportRag.Core.Ingest;
using SupportRag.Core.Options;
using SupportRag.Core.Pipeline;
using SupportRag.Core.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSupportRag(builder.Configuration).AddNightlyIngest();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgres");

// The Worker's per-IP limit, expressed with the framework's limiter: a fixed window per client address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("ask", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "That is a lot of questions in a short time. Please wait a minute and ask again." }, ct);
    };
});

// Traces, metrics and logs from the pipeline, ASP.NET Core, HttpClient (Anthropic, Azure OpenAI, Workers AI) and Npgsql.
// OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set; console in Development. The exporter reads the standard
// OTEL_EXPORTER_OTLP_* variables, so a backend is a matter of environment, not code: a local Datadog Agent
// (gRPC, no headers) or Dynatrace (https://<env>.live.dynatrace.com/api/v2/otlp, OTEL_EXPORTER_OTLP_PROTOCOL=
// http/protobuf, OTEL_EXPORTER_OTLP_HEADERS="Authorization=Api-Token <token>"). Metrics go out with delta
// temporality, which Dynatrace requires and every other OTLP backend accepts.
var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var deploymentEnvironment = builder.Configuration["DEPLOYMENT_ENVIRONMENT"] ?? builder.Environment.EnvironmentName.ToLowerInvariant();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService("support-rag", serviceNamespace: "fruitless-research", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString())
        .AddAttributes([
            new KeyValuePair<string, object>("deployment.environment.name", deploymentEnvironment),
            new KeyValuePair<string, object>("gen_ai.agent.name", SupportAssistant.AgentName),
        ]))
    .WithTracing(t =>
    {
        t.AddSource(Instrumentation.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql();
        if (otlp is not null) { t.AddOtlpExporter(); }
        else if (builder.Environment.IsDevelopment()) { t.AddConsoleExporter(); }
    })
    .WithMetrics(m =>
    {
        m.AddMeter(Instrumentation.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation();
        if (otlp is not null) { m.AddOtlpExporter((_, reader) => reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta); }
    })
    .WithLogging(l =>
    {
        if (otlp is not null) { l.AddOtlpExporter(); }
    }, o =>
    {
        o.IncludeFormattedMessage = true;
        o.IncludeScopes = true;
        o.ParseStateValues = true;
    });

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();

// Schema on boot. Migrations are the contract; EnsureCreated would silently diverge from them.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<RagDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/healthz");

app.MapGet("/", (IOptions<RagOptions> o) => Results.Ok(new
{
    name = "support-rag",
    description = "The fruitlessresearch.com support assistant, rebuilt on ASP.NET Core + Postgres/pgvector. POST /ask {\"question\": \"...\"}",
    model = o.Value.Model,
    embedder = o.Value.Embedder.ToString(),
    source = "https://github.com/twwalsh/dotnet-rag",
}));

app.MapPost("/ask", async Task<Results<Ok<AskResponse>, BadRequest<ErrorBody>>> (
    AskRequest request, SupportAssistant assistant, IOptions<RagOptions> o, CancellationToken ct) =>
{
    var question = (request.Question ?? "").Trim();
    if (question.Length == 0)
    {
        return TypedResults.BadRequest(new ErrorBody("Ask a question."));
    }

    if (question.Length > o.Value.QuestionMaxChars)
    {
        return TypedResults.BadRequest(new ErrorBody($"Please keep a question under {o.Value.QuestionMaxChars} characters."));
    }

    var r = await assistant.AskAsync(question, ct);
    return TypedResults.Ok(new AskResponse(
        r.Answer,
        r.Form,
        r.Sources,
        r.Passages.Select(p => new PassageRef(p.Id, p.Title, p.Url, p.Score)).ToList(),
        r.Model,
        r.Usage));
})
.RequireRateLimiting("ask")
.WithName("Ask")
.WithSummary("Answer a support question from the indexed documentation only.");

// Operator endpoint: re-ingest now. Idempotent; the nightly service calls the same code.
app.MapPost("/admin/ingest", async Task<Results<Ok<IngestReport>, UnauthorizedHttpResult>> (
    HttpRequest http, CorpusIngestor ingestor, IConfiguration config, CancellationToken ct) =>
{
    var expected = config["RAG_ADMIN_KEY"];
    if (string.IsNullOrEmpty(expected) || !http.Headers.TryGetValue("X-Admin-Key", out var given) || !string.Equals(given, expected, StringComparison.Ordinal))
    {
        return TypedResults.Unauthorized(); // fail closed: no key configured means no admin endpoint
    }

    return TypedResults.Ok(await ingestor.IngestAsync(ct));
})
.WithName("Ingest");

app.Run();

internal sealed record AskRequest(string? Question);

internal sealed record AskResponse(string Answer, string? Form, IReadOnlyList<SourceLink> Sources, IReadOnlyList<PassageRef> Passages, string? Model, Usage Usage);

internal sealed record PassageRef(string Id, string Title, string Url, double Score);

internal sealed record ErrorBody(string Error);
