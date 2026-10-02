using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CardTrader3000.Services.Inventory;

public enum InventorySort
{
    Updated,
    Player,
    Set,
    ListPrice,
    NetReturn,
    Margin,
    Quantity
}

/// <summary>Which statuses to show. "Active" (in stock + listed) is the default view.</summary>
public enum StatusFilter
{
    Active,
    InStock,
    Listed,
    Sold,
    All
}

public sealed record InventoryQuery
{
    public string? Search { get; init; }
    public string? CardSet { get; init; }
    public PriceBucket? Bucket { get; init; }
    public SgcCandidate? Sgc { get; init; }
    public StatusFilter Status { get; init; } = StatusFilter.Active;
    public bool RookiesOnly { get; init; }

    /// <summary>true = priced only, false = not priced (track-only) only, null = both.</summary>
    public bool? Priced { get; init; }

    public InventorySort SortBy { get; init; } = InventorySort.Updated;
    public bool Descending { get; init; } = true;

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record InventoryTotals(int DistinctCards, int Quantity, decimal ListValue, decimal NetValue);

public sealed record InventoryPage(IReadOnlyList<InventoryCard> Items, int TotalCount, int Page, int PageSize, InventoryTotals Totals)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

/// <summary>Server-side search, paging and edits for the inventory grid.</summary>
public sealed class InventoryService(IDbContextFactory<AppDbContext> dbFactory)
{
    public static readonly int[] PageSizes = [25, 50, 100];

    public async Task<InventoryPage> SearchAsync(InventoryQuery q, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = ApplyFilters(db.InventoryCards.AsNoTracking(), q);

        // Totals for the whole filtered set (not just this page). Three numeric columns per row
        // keeps this cheap at 10k+ cards and avoids SQL arithmetic on value-converted decimals.
        var sums = await query.Select(c => new { c.Quantity, c.EstimatedListPrice, c.MaxNetReturn }).ToListAsync(ct);
        var totals = new InventoryTotals(
            sums.Count,
            sums.Sum(s => s.Quantity),
            sums.Sum(s => s.EstimatedListPrice * s.Quantity),
            sums.Sum(s => s.MaxNetReturn * s.Quantity));

        var pageSize = PageSizes.Contains(q.PageSize) ? q.PageSize : PageSizes[0];
        var pageCount = Math.Max(1, (int)Math.Ceiling(sums.Count / (double)pageSize));
        var page = Math.Clamp(q.Page, 1, pageCount);

        var items = await ApplySort(query, q.SortBy, q.Descending)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new InventoryPage(items, sums.Count, page, pageSize, totals);
    }

    /// <summary>Distinct set names for the filter dropdown.</summary>
    public async Task<List<string>> GetSetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.InventoryCards.Select(c => c.CardSet).Distinct().OrderBy(s => s).ToListAsync(ct);
    }

    /// <summary>One card with its import history (newest first).</summary>
    public async Task<InventoryCard?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await db.InventoryCards.AsNoTracking()
            .Include(c => c.ImportItems).ThenInclude(i => i.ImportBatch)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        card?.ImportItems.Sort((a, b) => b.ImportBatchId.CompareTo(a.ImportBatchId));
        return card;
    }

    /// <summary>Updates quantity and status. A quantity of 0 marks the card Sold.</summary>
    public async Task<InventoryCard?> UpdateStockAsync(int id, int quantity, InventoryStatus status, CancellationToken ct = default)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await db.InventoryCards.FindAsync([id], ct);
        if (card is null) return null;

        card.Quantity = quantity;
        card.Status = quantity == 0 ? InventoryStatus.Sold : status;
        card.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return card;
    }

    /// <summary>Updates the listing text after manual edits. The title is capped at 80 characters.</summary>
    public async Task<bool> UpdateListingAsync(int id, string title, string description, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await db.InventoryCards.FindAsync([id], ct);
        if (card is null) return false;

        title = title.Trim();
        card.EbayTitle = title.Length > 80 ? title[..80].TrimEnd() : title;
        card.EbayDescription = description.Trim();
        card.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deletes a card. Its import history lines are kept but unlinked.</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.InventoryCards.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    private static IQueryable<InventoryCard> ApplyFilters(IQueryable<InventoryCard> query, InventoryQuery q)
    {
        query = q.Status switch
        {
            StatusFilter.Active => query.Where(c => c.Status != InventoryStatus.Sold),
            StatusFilter.InStock => query.Where(c => c.Status == InventoryStatus.InStock),
            StatusFilter.Listed => query.Where(c => c.Status == InventoryStatus.Listed),
            StatusFilter.Sold => query.Where(c => c.Status == InventoryStatus.Sold),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // SQLite LIKE is case-insensitive for ASCII. Each word must match somewhere.
            foreach (var word in q.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6))
            {
                var pattern = $"%{EscapeLike(word)}%";
                query = query.Where(c =>
                    EF.Functions.Like(c.PlayerName, pattern, "\\") ||
                    EF.Functions.Like(c.CardSet, pattern, "\\") ||
                    EF.Functions.Like(c.CardNumber, pattern, "\\") ||
                    EF.Functions.Like(c.Parallel, pattern, "\\") ||
                    (c.Team != null && EF.Functions.Like(c.Team, pattern, "\\")) ||
                    (c.EbayTitle != null && EF.Functions.Like(c.EbayTitle, pattern, "\\")));
            }
        }

        if (!string.IsNullOrWhiteSpace(q.CardSet)) query = query.Where(c => c.CardSet == q.CardSet);
        // Unpriced cards carry placeholder bucket/SGC values, so these filters only match priced cards.
        if (q.Bucket is { } bucket) query = query.Where(c => c.LastPricedUtc != null && c.Bucket == bucket);
        if (q.Sgc is { } sgc) query = query.Where(c => c.LastPricedUtc != null && c.SgcCandidate == sgc);
        if (q.Priced is { } priced) query = priced ? query.Where(c => c.LastPricedUtc != null) : query.Where(c => c.LastPricedUtc == null);
        if (q.RookiesOnly) query = query.Where(c => c.IsRookie);

        return query;
    }

    private static IQueryable<InventoryCard> ApplySort(IQueryable<InventoryCard> query, InventorySort sort, bool desc)
    {
        IOrderedQueryable<InventoryCard> ordered = (sort, desc) switch
        {
            (InventorySort.Player, false) => query.OrderBy(c => c.PlayerName),
            (InventorySort.Player, true) => query.OrderByDescending(c => c.PlayerName),
            (InventorySort.Set, false) => query.OrderBy(c => c.CardSet).ThenBy(c => c.CardNumber),
            (InventorySort.Set, true) => query.OrderByDescending(c => c.CardSet).ThenBy(c => c.CardNumber),
            (InventorySort.ListPrice, false) => query.OrderBy(c => c.EstimatedListPrice),
            (InventorySort.ListPrice, true) => query.OrderByDescending(c => c.EstimatedListPrice),
            (InventorySort.NetReturn, false) => query.OrderBy(c => c.MaxNetReturn),
            (InventorySort.NetReturn, true) => query.OrderByDescending(c => c.MaxNetReturn),
            (InventorySort.Margin, false) => query.OrderBy(c => c.NetMarginPercent),
            (InventorySort.Margin, true) => query.OrderByDescending(c => c.NetMarginPercent),
            (InventorySort.Quantity, false) => query.OrderBy(c => c.Quantity),
            (InventorySort.Quantity, true) => query.OrderByDescending(c => c.Quantity),
            (_, false) => query.OrderBy(c => c.UpdatedUtc),
            _ => query.OrderByDescending(c => c.UpdatedUtc)
        };

        // Stable paging when many rows share the sort value.
        return ordered.ThenBy(c => c.Id);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
