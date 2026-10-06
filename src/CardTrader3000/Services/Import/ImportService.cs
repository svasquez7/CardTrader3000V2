using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using CardTrader3000.Services.Claude;
using CardTrader3000.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Import;

public sealed record SubmitResult(int? BatchId, ParseResult Parse)
{
    public bool Accepted => BatchId is not null;
}

/// <summary>
/// Entry point for the UI: parses input, creates an <see cref="ImportBatch"/> and queues it.
/// The actual Claude calls and inventory merge happen in <see cref="ImportProcessor"/> on the worker.
/// </summary>
public sealed class ImportService(
    IDbContextFactory<AppDbContext> dbFactory,
    ImportQueue queue,
    ImportProgressTracker tracker,
    FeeCalculator fees,
    IOptions<ClaudeOptions> claudeOptions,
    ILogger<ImportService> logger)
{
    /// <summary>Upper bound on uploaded file size, enforced by the UI when reading the browser stream.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Parse only. Used for the preview before submitting.</summary>
    /// <param name="priceByDefault">Price flag for lines that don't have their own 5th column.</param>
    public ParseResult Preview(string? text, bool priceByDefault = true) => CardLineParser.Parse(text, priceByDefault);

    /// <summary>Parse a JSON import for the preview.</summary>
    public ParseResult PreviewJson(string? json, bool priceByDefault = true) =>
        JsonCardParser.Parse(json, priceByDefault, fees.Rates.MaxTitleLength);

    public Task<SubmitResult> SubmitCsvAsync(string csvText, string fileName, bool priceByDefault = true, CancellationToken ct = default) =>
        SubmitAsync(ImportSource.Csv, Path.GetFileName(fileName), CardLineParser.Parse(csvText, priceByDefault), ct);

    public Task<SubmitResult> SubmitManualAsync(string text, bool priceByDefault = true, CancellationToken ct = default) =>
        SubmitAsync(ImportSource.Manual, null, CardLineParser.Parse(text, priceByDefault), ct);

    /// <summary>
    /// JSON import. Cards with a list price are stored as given (no Claude call); cards without one
    /// are priced by Claude or added track-only per their flag. Any provided field wins over Claude's.
    /// </summary>
    public Task<SubmitResult> SubmitJsonAsync(string json, string fileName, bool priceByDefault = true, CancellationToken ct = default) =>
        SubmitAsync(ImportSource.Json, Path.GetFileName(fileName), PreviewJson(json, priceByDefault), ct);

    private async Task<SubmitResult> SubmitAsync(ImportSource source, string? fileName, ParseResult parse, CancellationToken ct)
    {
        if (parse.Lines.Count == 0)
            return new SubmitResult(null, parse);

        var distinct = parse.Distinct();
        var rates = fees.Rates;

        var batch = new ImportBatch
        {
            Source = source,
            SourceFileName = fileName,
            Status = ImportBatchStatus.Pending,
            CreatedUtc = DateTime.UtcNow,
            TotalInputLines = parse.Lines.Count + parse.Errors.Count,
            FailedCardCount = 0,
            FinalValueFeeRate = rates.FinalValueFeeRate,
            FixedOrderFee = rates.FixedOrderFee,
            StandardEnvelopeRate = rates.StandardEnvelopeRate,
            Model = claudeOptions.Value.Model,
            ErrorMessage = DescribeSkippedLines(parse.Errors),
            Items = distinct.Select(d => new ImportBatchItem
            {
                LineNumber = d.First.LineNumber,
                CardSet = d.First.CardSet,
                PlayerName = d.First.PlayerName,
                CardNumber = d.First.CardNumber,
                Parallel = d.First.Parallel,
                QuantityAdded = d.Quantity,
                // A price supplied in the file always wins: no Claude call, and not "track only".
                PricingProvided = d.First.Data?.ListPrice is not null,
                SkipPricing = !d.Price && d.First.Data?.ListPrice is null,
                ProvidedDataJson = d.First.Data?.Serialize(),
                Status = ImportItemStatus.Pending
            }).ToList()
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.ImportBatches.Add(batch);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Queued {Source} batch {BatchId}: {Lines} lines, {Distinct} distinct cards ({Provided} with provided pricing, {TrackOnly} track-only), {Skipped} skipped",
            source, batch.Id, parse.Lines.Count, distinct.Count,
            batch.Items.Count(i => i.PricingProvided), batch.Items.Count(i => i.SkipPricing), parse.Errors.Count);

        tracker.Report(new ImportProgress(batch.Id, ImportBatchStatus.Pending, 0, batch.Items.Count, "Queued"));
        await queue.EnqueueAsync(batch.Id, ct);

        return new SubmitResult(batch.Id, parse);
    }

    /// <summary>
    /// Sends existing inventory cards back through Claude to refresh pricing and listing text.
    /// Runs as its own batch (Source = Reprice) so it shows in History with its token usage.
    /// Stock is untouched (QuantityAdded = 0). Manually edited titles/descriptions are replaced.
    /// </summary>
    /// <returns>The new batch id, or null if none of the ids exist.</returns>
    public async Task<int?> RepriceAsync(IEnumerable<int> cardIds, CancellationToken ct = default)
    {
        var ids = cardIds.Distinct().ToList();
        if (ids.Count == 0) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cards = await db.InventoryCards.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
        if (cards.Count == 0) return null;

        var rates = fees.Rates;
        var line = 0;
        var batch = new ImportBatch
        {
            Source = ImportSource.Reprice,
            Status = ImportBatchStatus.Pending,
            CreatedUtc = DateTime.UtcNow,
            TotalInputLines = cards.Count,
            FinalValueFeeRate = rates.FinalValueFeeRate,
            FixedOrderFee = rates.FixedOrderFee,
            StandardEnvelopeRate = rates.StandardEnvelopeRate,
            Model = claudeOptions.Value.Model,
            Items = cards.Select(c => new ImportBatchItem
            {
                LineNumber = ++line,
                InventoryCardId = c.Id,
                CardSet = c.CardSet,
                PlayerName = c.PlayerName,
                CardNumber = c.CardNumber,
                Parallel = c.Parallel,
                QuantityAdded = 0,
                Status = ImportItemStatus.Pending
            }).ToList()
        };

        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Queued re-price batch {BatchId} for {Count} card(s)", batch.Id, cards.Count);

        tracker.Report(new ImportProgress(batch.Id, ImportBatchStatus.Pending, 0, cards.Count, "Queued"));
        await queue.EnqueueAsync(batch.Id, ct);
        return batch.Id;
    }

    /// <summary>Card ids with a re-price still pending, grouped by batch. Lets the UI show work in progress after a reload.</summary>
    public async Task<Dictionary<int, List<int>>> GetPendingRepricesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.ImportBatchItems.AsNoTracking()
            .Where(i => i.Status == ImportItemStatus.Pending
                        && i.InventoryCardId != null
                        && i.ImportBatch.Source == ImportSource.Reprice)
            .Select(i => new { i.ImportBatchId, CardId = i.InventoryCardId!.Value })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.ImportBatchId)
                   .ToDictionary(g => g.Key, g => g.Select(r => r.CardId).ToList());
    }

    /// <summary>Re-queues a batch's failed cards (e.g. after fixing the API key or an outage).</summary>
    public async Task<bool> RetryFailedAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var batch = await db.ImportBatches.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null || batch.Status is ImportBatchStatus.Pending or ImportBatchStatus.Processing)
            return false;

        var failed = batch.Items.Where(i => i.Status == ImportItemStatus.Failed).ToList();
        if (failed.Count == 0) return false;

        foreach (var item in failed)
        {
            item.Status = ImportItemStatus.Pending;
            item.ErrorMessage = null;
        }

        batch.Status = ImportBatchStatus.Pending;
        batch.CompletedUtc = null;
        await db.SaveChangesAsync(ct);

        tracker.Report(new ImportProgress(batchId, ImportBatchStatus.Pending,
            batch.Items.Count - failed.Count, batch.Items.Count, $"Retrying {failed.Count} card(s)"));
        await queue.EnqueueAsync(batchId, ct);
        return true;
    }

    private static string? DescribeSkippedLines(IReadOnlyList<LineError> errors)
    {
        if (errors.Count == 0) return null;

        const int show = 10;
        var lines = errors.Take(show).Select(e => $"Line {e.LineNumber}: {e.Message}");
        var more = errors.Count > show ? $"\n…and {errors.Count - show} more." : "";
        return $"{errors.Count} line(s) skipped:\n{string.Join('\n', lines)}{more}";
    }
}
