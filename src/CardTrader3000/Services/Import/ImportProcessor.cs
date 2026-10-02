using System.Text.Json;
using System.Text.Json.Nodes;
using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using CardTrader3000.Services.Claude;
using CardTrader3000.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Import;

/// <summary>
/// Processes one batch. Track-only lines (Price = false) are merged into inventory first with no
/// Claude call. The rest go to Claude a chunk at a time; each chunk is merged and saved before the
/// next, so an interrupted batch resumes where it stopped (pending items are simply picked up again).
/// Re-price lines (QuantityAdded = 0) refresh pricing and listing text without touching stock.
/// </summary>
public sealed class ImportProcessor(
    IDbContextFactory<AppDbContext> dbFactory,
    IClaudeCardEvaluator evaluator,
    FeeCalculator fees,
    ImportProgressTracker tracker,
    IOptions<ClaudeOptions> claudeOptions,
    ILogger<ImportProcessor> logger)
{
    public async Task ProcessAsync(int batchId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null)
        {
            logger.LogWarning("Import batch {BatchId} not found", batchId);
            return;
        }

        var pending = await db.ImportBatchItems
            .Where(i => i.ImportBatchId == batchId && i.Status == ImportItemStatus.Pending)
            .OrderBy(i => i.LineNumber)
            .ToListAsync(ct);

        var total = await db.ImportBatchItems.CountAsync(i => i.ImportBatchId == batchId, ct);
        var done = total - pending.Count;

        batch.Status = ImportBatchStatus.Processing;
        batch.StartedUtc ??= DateTime.UtcNow;
        batch.Model = claudeOptions.Value.Model;
        await db.SaveChangesAsync(ct);
        Report(batch, done, total, pending.Count == 0 ? "Finalizing" : "Starting");

        // ---- Track-only lines: inventory only, no AI cost ----
        var trackOnly = pending.Where(i => i.SkipPricing).ToList();
        if (trackOnly.Count > 0)
        {
            await MergeTrackOnlyAsync(db, trackOnly, ct);
            await db.SaveChangesAsync(ct);
            done += trackOnly.Count;
            Report(batch, done, total, $"Added {trackOnly.Count} card(s) without pricing");
        }

        // ---- Re-price lines whose card was deleted after the batch was queued ----
        var toPrice = new List<ImportBatchItem>();
        foreach (var item in pending.Where(i => !i.SkipPricing))
        {
            if (item.QuantityAdded == 0 && item.InventoryCardId is null)
            {
                item.Status = ImportItemStatus.Failed;
                item.ErrorMessage = "This card was deleted from inventory before it could be re-priced.";
                done++;
            }
            else
            {
                toPrice.Add(item);
            }
        }

        var rawResponses = LoadRawResponses(batch.RawResponseJson);
        var chunkSize = Math.Max(1, claudeOptions.Value.ChunkSize);

        foreach (var slice in toPrice.Chunk(chunkSize))
        {
            ct.ThrowIfCancellationRequested();

            var inputs = slice
                .Select(i => new CardInput(i.Id, i.CardSet, i.PlayerName, i.CardNumber, i.Parallel))
                .ToList();

            EvaluationRunResult result;
            try
            {
                result = await evaluator.EvaluateAsync(inputs, progress: null, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Configuration problem (e.g. missing API key): every remaining card would fail the same way.
                logger.LogError(ex, "Import batch {BatchId} stopped", batchId);
                foreach (var item in toPrice.Where(i => i.Status == ImportItemStatus.Pending))
                {
                    item.Status = ImportItemStatus.Failed;
                    item.ErrorMessage = ex.Message;
                }
                batch.ErrorMessage = AppendError(batch.ErrorMessage, ex.Message);
                await FinalizeAsync(db, batch, ct);
                return;
            }

            await MergeIntoInventoryAsync(db, slice, result, ct);

            batch.ChunkCount += result.RequestCount;
            batch.InputTokens += result.InputTokens;
            batch.OutputTokens += result.OutputTokens;
            foreach (var raw in result.RawResponses) rawResponses.Add(ParseOrString(raw));
            batch.RawResponseJson = rawResponses.ToJsonString();

            await db.SaveChangesAsync(ct);

            done += slice.Length;
            Report(batch, done, total, $"Evaluated {done} of {total} cards");
        }

        await FinalizeAsync(db, batch, ct);
    }

    /// <summary>
    /// Applies a chunk's results. Existing cards (same normalized key) get their quantity increased
    /// and pricing refreshed; new cards are inserted. Each batch item records what happened.
    /// </summary>
    private async Task MergeIntoInventoryAsync(AppDbContext db, ImportBatchItem[] slice, EvaluationRunResult result, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var itemsById = slice.ToDictionary(i => i.Id);

        var keys = slice.Select(KeyOf).Distinct().ToList();
        var inventory = await db.InventoryCards
            .Where(c => keys.Contains(c.NormalizedKey))
            .ToDictionaryAsync(c => c.NormalizedKey, ct);

        foreach (var ok in result.Succeeded)
        {
            var item = itemsById[ok.Input.Id];
            var key = KeyOf(item);

            if (inventory.TryGetValue(key, out var card))
            {
                AddStock(card, item.QuantityAdded);
                item.CreatedNewCard = false;
            }
            else if (item.QuantityAdded == 0)
            {
                // Re-price of a card deleted while Claude was working on it: don't resurrect it with 0 stock.
                item.Status = ImportItemStatus.Failed;
                item.ErrorMessage = "This card was deleted from inventory before it could be re-priced.";
                continue;
            }
            else
            {
                card = new InventoryCard
                {
                    CardSet = item.CardSet,
                    PlayerName = item.PlayerName,
                    CardNumber = item.CardNumber,
                    Parallel = item.Parallel,
                    NormalizedKey = key,
                    Quantity = item.QuantityAdded,
                    Status = InventoryStatus.InStock,
                    CreatedUtc = now
                };
                db.InventoryCards.Add(card);
                inventory[key] = card;
                item.CreatedNewCard = true;
            }

            ApplyEvaluation(card, ok, now);

            item.InventoryCard = card;
            item.Status = ImportItemStatus.Succeeded;
            item.ErrorMessage = null;
            item.EstimatedListPrice = ok.Fees.ListPrice;
            item.MaxNetReturn = ok.Fees.MaxNetReturn;
            item.Bucket = ok.Fees.Bucket;
            item.SgcCandidate = ok.SgcCandidate;
        }

        foreach (var failure in result.Failed)
        {
            var item = itemsById[failure.Input.Id];
            item.Status = ImportItemStatus.Failed;
            item.ErrorMessage = Truncate(failure.Error, 1000);
        }
    }

    /// <summary>
    /// Track-only lines: increase the quantity of an existing card (its pricing is left as is), or
    /// add a new card with no pricing. No Claude call, so no AI cost.
    /// </summary>
    private async Task MergeTrackOnlyAsync(AppDbContext db, List<ImportBatchItem> items, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var keys = items.Select(KeyOf).Distinct().ToList();
        var inventory = await db.InventoryCards
            .Where(c => keys.Contains(c.NormalizedKey))
            .ToDictionaryAsync(c => c.NormalizedKey, ct);

        foreach (var item in items)
        {
            var key = KeyOf(item);
            if (inventory.TryGetValue(key, out var card))
            {
                AddStock(card, item.QuantityAdded);
                card.UpdatedUtc = now;
                item.CreatedNewCard = false;
            }
            else
            {
                card = new InventoryCard
                {
                    CardSet = item.CardSet,
                    PlayerName = item.PlayerName,
                    CardNumber = item.CardNumber,
                    Parallel = item.Parallel,
                    NormalizedKey = key,
                    Quantity = item.QuantityAdded,
                    Status = InventoryStatus.InStock,
                    // Unpriced: LastPricedUtc stays null, money stays 0. Bucket/SGC are placeholders
                    // and are ignored by filters and badges until the card is priced.
                    Bucket = PriceBucket.Bucket2,
                    SgcCandidate = SgcCandidate.No,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                db.InventoryCards.Add(card);
                inventory[key] = card;
                item.CreatedNewCard = true;
            }

            item.InventoryCard = card;
            item.Status = ImportItemStatus.Succeeded;
            item.ErrorMessage = null;
        }
    }

    /// <summary>Adds copies to a card. A re-price adds 0 and leaves stock alone.</summary>
    private static void AddStock(InventoryCard card, int quantity)
    {
        if (quantity <= 0) return;

        if (card.Status == InventoryStatus.Sold)
        {
            // Previously sold out: this is a restock, not an addition to old copies.
            card.Quantity = quantity;
            card.Status = InventoryStatus.InStock;
        }
        else
        {
            card.Quantity += quantity;
        }
    }

    private static void ApplyEvaluation(InventoryCard card, EvaluatedCard e, DateTime now)
    {
        card.Team = e.Team;
        card.IsRookie = e.IsRookie;
        card.Bucket = e.Fees.Bucket;
        card.SgcCandidate = e.SgcCandidate;
        card.SalesStrategy = e.SalesStrategy;
        card.EbayTitle = e.EbayTitle;
        card.EbayDescription = e.EbayDescription;

        card.EstimatedListPrice = e.Fees.ListPrice;
        card.EbayFee = e.Fees.EbayFee;
        card.PostageCost = e.Fees.PostageCost;
        card.TotalFees = e.Fees.TotalFees;
        card.MaxNetReturn = e.Fees.MaxNetReturn;
        card.NetMarginPercent = e.Fees.NetMarginPercent;

        card.LastPricedUtc = now;
        card.UpdatedUtc = now;
    }

    /// <summary>Recomputes all batch counts and totals from its items, so retries stay consistent.</summary>
    private async Task FinalizeAsync(AppDbContext db, ImportBatch batch, CancellationToken ct)
    {
        var items = await db.ImportBatchItems
            .Where(i => i.ImportBatchId == batch.Id)
            .ToListAsync(ct);

        var completed = items.Where(i => i.Status == ImportItemStatus.Succeeded).ToList();
        var priced = completed.Where(i => !i.SkipPricing && i.EstimatedListPrice is not null).ToList();
        var failed = items.Count(i => i.Status == ImportItemStatus.Failed);

        // Totals cover priced cards only. Re-price lines add no stock, so they count one copy each.
        static int Copies(ImportBatchItem i) => i.QuantityAdded > 0 ? i.QuantityAdded : 1;
        var totals = fees.Summarize(priced.Select(i => (fees.Calculate(i.EstimatedListPrice!.Value), Copies(i))));

        batch.TotalCardsEvaluated = priced.Sum(Copies);
        batch.NewCardCount = completed.Count(i => i.CreatedNewCard);
        batch.MergedCardCount = completed.Count(i => !i.CreatedNewCard && i.QuantityAdded > 0);
        batch.UnpricedCardCount = completed.Where(i => i.SkipPricing).Sum(i => i.QuantityAdded);
        batch.FailedCardCount = failed;
        batch.Bucket1Count = totals.Bucket1Count;
        batch.Bucket2Count = totals.Bucket2Count;
        batch.TotalGrossRevenue = totals.TotalGrossRevenue;
        batch.TotalEbayFees = totals.TotalEbayFees;
        batch.TotalPostage = totals.TotalPostage;
        batch.TotalMaxNetReturn = totals.TotalMaxNetReturn;
        batch.OverallNetMarginPercent = totals.OverallNetMarginPercent;

        batch.Status = (completed.Count, failed) switch
        {
            (0, > 0) => ImportBatchStatus.Failed,
            (_, > 0) => ImportBatchStatus.CompletedWithErrors,
            _ => ImportBatchStatus.Completed
        };
        batch.CompletedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Import batch {BatchId} {Status}: {Priced} priced, {Unpriced} track-only copies, {New} new, {Merged} merged, {Failed} failed, net {Net:C}",
            batch.Id, batch.Status, priced.Count, batch.UnpricedCardCount, batch.NewCardCount, batch.MergedCardCount, failed, batch.TotalMaxNetReturn);

        var trackOnlyNote = batch.UnpricedCardCount > 0 ? $", {batch.UnpricedCardCount} added without pricing" : "";
        var message = batch.Status switch
        {
            ImportBatchStatus.Completed => $"Done: {priced.Count} card(s) priced{trackOnlyNote}",
            ImportBatchStatus.CompletedWithErrors => $"Done with errors: {priced.Count} priced{trackOnlyNote}, {failed} failed",
            _ => $"Failed: {failed} card(s) could not be evaluated"
        };
        Report(batch, items.Count, items.Count, message);
    }

    private void Report(ImportBatch batch, int done, int total, string message) =>
        tracker.Report(new ImportProgress(batch.Id, batch.Status, done, total, message));

    private static string KeyOf(ImportBatchItem i) => CardKey.Normalize(i.CardSet, i.PlayerName, i.CardNumber, i.Parallel);

    private static JsonArray LoadRawResponses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonArray();
        try { return JsonNode.Parse(json) as JsonArray ?? new JsonArray(); }
        catch (JsonException) { return new JsonArray(); }
    }

    /// <summary>Stores valid JSON as an object (readable when inspecting the DB), anything else as a string.</summary>
    private static JsonNode? ParseOrString(string raw)
    {
        try { return JsonNode.Parse(raw); }
        catch (JsonException) { return JsonValue.Create(raw); }
    }

    private static string AppendError(string? existing, string error) =>
        string.IsNullOrWhiteSpace(existing) ? error : $"{existing}\n\n{error}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
