using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportRag.Core.Options;

namespace SupportRag.Core.Ingest;

/// <summary>
/// Re-ingests the corpus once a day at a fixed UTC hour, and on startup when the table is empty. The ingest
/// is idempotent, so running it after a crash, a redeploy or a missed night is harmless. Scoped services
/// (the DbContext) are resolved per run, which is the one rule a BackgroundService has to respect.
/// </summary>
public sealed class NightlyIngestService(IServiceScopeFactory scopes, IOptions<RagOptions> options, TimeProvider clock, ILogger<NightlyIngestService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.IngestOnStartupIfEmpty)
        {
            await RunIfAsync(ingestor => ingestor.IsEmptyAsync(stoppingToken), "table empty at startup", stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = UntilNextRun(clock.GetUtcNow(), options.Value.IngestHourUtc);
            log.LogInformation("Next scheduled ingest in {Hours:F1}h", delay.TotalHours);
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunIfAsync(_ => Task.FromResult(true), "scheduled", stoppingToken);
        }
    }

    internal static TimeSpan UntilNextRun(DateTimeOffset now, int hourUtc)
    {
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, hourUtc, 0, 0, TimeSpan.Zero);
        if (next <= now)
        {
            next = next.AddDays(1);
        }

        return next - now;
    }

    private async Task RunIfAsync(Func<CorpusIngestor, Task<bool>> condition, string reason, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var ingestor = scope.ServiceProvider.GetRequiredService<CorpusIngestor>();
            if (await condition(ingestor))
            {
                log.LogInformation("Ingest starting ({Reason})", reason);
                await ingestor.IngestAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed nightly run must not take the API down; the next run, or an operator POST, retries.
            log.LogError(ex, "Ingest failed ({Reason})", reason);
        }
    }
}
