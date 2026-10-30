// The 30-case evaluation set, run in-process against the same pipeline the API serves.
//
//   dotnet run --project src/SupportRag.Eval                 # all cases, summary + every miss
//   dotnet run --project src/SupportRag.Eval -- --json eval/latest.json --min 0.8
//   dotnet run --project src/SupportRag.Eval -- --ingest             # migrate + ingest first (CI, a fresh database)
//
// The cases are data/eval.jsonl, copied verbatim from the Cloudflare build: a question, `must` regexes
// the answer has to match, `mustNot` ones it must not, and sometimes the self-service `form` it has to
// offer. A case passes strictly when every one of its rules holds, which is how all three builds report,
// so a .NET score, a Databricks score and a Cloudflare score mean the same thing. Exit code 1 below --min.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SupportRag.Core;
using SupportRag.Core.Data;
using SupportRag.Core.Ingest;
using SupportRag.Core.Pipeline;

var jsonOut = Arg("--json");
var minPass = double.Parse(Arg("--min") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
var casesPath = Arg("--cases") ?? "data/eval.jsonl";

var host = Host.CreateApplicationBuilder(args);
// The generic host reads appsettings.json from the working directory; this tool is run from the repo root,
// so its own settings file (copied beside the binary) is added explicitly. Environment variables still win.
host.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
host.Configuration.AddEnvironmentVariables();
host.Logging.SetMinimumLevel(LogLevel.Warning);
host.Services.AddSupportRag(host.Configuration);
using var app = host.Build();

if (Environment.GetCommandLineArgs().Contains("--ingest"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<RagDbContext>().Database.MigrateAsync();
    var report = await scope.ServiceProvider.GetRequiredService<CorpusIngestor>().IngestAsync(CancellationToken.None);
    Console.WriteLine($"ingest: {report.Seen} seen, {report.Embedded} embedded, {report.Unchanged} unchanged, {report.Deleted} deleted ({report.EmbeddingModel}, {report.Duration.TotalSeconds:F1}s)");
}

var cases = File.ReadLines(casesPath)
    .Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(l => JsonSerializer.Deserialize<EvalCase>(l, EvalJson.Options)!)
    .ToList();

var results = new List<CaseResult>(cases.Count);
foreach (var c in cases)
{
    using var scope = app.Services.CreateScope();
    var assistant = scope.ServiceProvider.GetRequiredService<SupportAssistant>();
    var r = await assistant.AskAsync(c.Q, CancellationToken.None);
    var problems = new List<string>();
    problems.AddRange(c.Must.Where(p => !Regex.IsMatch(r.Answer, p, RegexOptions.IgnoreCase)).Select(p => $"missing /{p}/"));
    problems.AddRange(c.MustNot.Where(p => Regex.IsMatch(r.Answer, p, RegexOptions.IgnoreCase)).Select(p => $"said /{p}/"));
    if (c.Form is not null && r.Form != c.Form)
    {
        problems.Add($"form: wanted {c.Form}, got {r.Form ?? "none"}");
    }

    results.Add(new CaseResult(c.Q, r.Answer, r.Form, r.Sources.Select(s => s.Title).ToList(), r.Passages.Select(p => p.Title).ToList(), problems, r.Usage));
    Console.Write(problems.Count == 0 ? "." : "F");
}

Console.WriteLine();
var must = results.Count(r => !r.Problems.Any(p => p.StartsWith("missing", StringComparison.Ordinal)));
var mustNot = results.Count(r => !r.Problems.Any(p => p.StartsWith("said", StringComparison.Ordinal)));
var forms = results.Count(r => !r.Problems.Any(p => p.StartsWith("form", StringComparison.Ordinal)));
var strict = results.Count(r => r.Problems.Count == 0);
var n = results.Count;
Console.WriteLine($"""
    must-say rules met          {must}/{n}
    must-not-say rules met      {mustNot}/{n}
    right form offered          {forms}/{n}
    strict pass                 {strict}/{n}
    tokens in/out               {results.Sum(r => r.Usage.InputTokens)}/{results.Sum(r => r.Usage.OutputTokens)}  (cache reads {results.Sum(r => r.Usage.CacheReadTokens)})
    mean seconds per question   {results.Average(r => r.Usage.Seconds):F2}
    """);
foreach (var r in results.Where(r => r.Problems.Count > 0))
{
    Console.WriteLine($"\nFAIL  {r.Question}\n      {string.Join("; ", r.Problems)}\n      form={r.Form ?? "none"} answer: {r.Answer.ReplaceLineEndings(" ")[..Math.Min(300, r.Answer.Length)]}");
}

if (jsonOut is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
    await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(new { ranAt = DateTimeOffset.UtcNow, strict, n, must, mustNot, forms, results }, EvalJson.Options));
}

var rate = (double)strict / n;
return rate >= minPass ? 0 : 1;

static string? Arg(string name)
{
    var argv = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

internal sealed record EvalCase(
    [property: JsonPropertyName("q")] string Q,
    [property: JsonPropertyName("must")] List<string> Must,
    [property: JsonPropertyName("mustNot")] List<string> MustNot,
    [property: JsonPropertyName("form")] string? Form);

internal sealed record CaseResult(string Question, string Answer, string? Form, List<string> Sources, List<string> Passages, List<string> Problems, Usage Usage);

internal static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
