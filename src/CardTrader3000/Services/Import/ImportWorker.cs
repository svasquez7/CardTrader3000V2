using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CardTrader3000.Services.Import;

/// <summary>
/// Background service that drains <see cref="ImportQueue"/> one batch at a time. Imports keep running
/// if the user navigates away or closes the tab. On startup it re-queues batches that were pending
/// or interrupted mid-run (e.g. the app was stopped), and they resume from their pending cards.
/// </summary>
public sealed class ImportWorker(
    ImportQueue queue,
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await foreach (var batchId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ImportProcessor>();
                await processor.ProcessAsync(batchId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Import batch {BatchId} interrupted by shutdown; it will resume on next start", batchId);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import batch {BatchId} crashed", batchId);
                await MarkFailedAsync(batchId, ex.Message);
            }
        }
    }

    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.ImportBatches
            .Where(b => b.Status == ImportBatchStatus.Pending || b.Status == ImportBatchStatus.Processing)
            .OrderBy(b => b.Id)
            .Select(b => b.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            logger.LogInformation("Resuming unfinished import batch {BatchId}", id);
            await queue.EnqueueAsync(id, ct);
        }
    }

    private async Task MarkFailedAsync(int batchId, string error)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var batch = await db.ImportBatches.FindAsync(batchId);
            if (batch is null) return;

            batch.Status = ImportBatchStatus.Failed;
            batch.CompletedUtc = DateTime.UtcNow;
            batch.ErrorMessage = string.IsNullOrWhiteSpace(batch.ErrorMessage) ? error : $"{batch.ErrorMessage}\n\n{error}";
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark batch {BatchId} as failed", batchId);
        }
    }
}
