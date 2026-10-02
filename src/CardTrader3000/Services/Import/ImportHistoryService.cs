using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CardTrader3000.Services.Import;

public sealed record BatchPage(IReadOnlyList<ImportBatch> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

/// <summary>Read side of import history for the History pages.</summary>
public sealed class ImportHistoryService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<BatchPage> ListAsync(int page, int pageSize, ImportBatchStatus? status = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.ImportBatches.AsNoTracking();
        if (status is { } s) query = query.Where(b => b.Status == s);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        // RawResponseJson can be large, so it's excluded from the list query.
        var items = await query
            .OrderByDescending(b => b.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new ImportBatch
            {
                Id = b.Id,
                Source = b.Source,
                SourceFileName = b.SourceFileName,
                Status = b.Status,
                CreatedUtc = b.CreatedUtc,
                StartedUtc = b.StartedUtc,
                CompletedUtc = b.CompletedUtc,
                TotalInputLines = b.TotalInputLines,
                TotalCardsEvaluated = b.TotalCardsEvaluated,
                NewCardCount = b.NewCardCount,
                MergedCardCount = b.MergedCardCount,
                FailedCardCount = b.FailedCardCount,
                UnpricedCardCount = b.UnpricedCardCount,
                Bucket1Count = b.Bucket1Count,
                Bucket2Count = b.Bucket2Count,
                TotalGrossRevenue = b.TotalGrossRevenue,
                TotalEbayFees = b.TotalEbayFees,
                TotalPostage = b.TotalPostage,
                TotalMaxNetReturn = b.TotalMaxNetReturn,
                OverallNetMarginPercent = b.OverallNetMarginPercent,
                Model = b.Model,
                InputTokens = b.InputTokens,
                OutputTokens = b.OutputTokens
            })
            .ToListAsync(ct);

        return new BatchPage(items, total, page, pageSize);
    }

    public async Task<ImportBatch?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<List<ImportBatchItem>> GetItemsAsync(int batchId, ImportItemStatus? status = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.ImportBatchItems.AsNoTracking().Where(i => i.ImportBatchId == batchId);
        if (status is { } s) query = query.Where(i => i.Status == s);

        return await query.OrderBy(i => i.LineNumber).ToListAsync(ct);
    }
}
