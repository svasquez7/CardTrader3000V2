using CardTrader3000.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CardTrader3000.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<InventoryCard> InventoryCards => Set<InventoryCard>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportBatchItem> ImportBatchItems => Set<ImportBatchItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite stores decimal as TEXT, which can't be sorted or summed in SQL.
        // Store money as REAL so the inventory grid can ORDER BY / SUM server-side.
        configurationBuilder.Properties<decimal>().HaveConversion<double>();

        // Readable enum values in the database.
        configurationBuilder.Properties<PriceBucket>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<SgcCandidate>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<InventoryStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ImportSource>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ImportBatchStatus>().HaveConversion<string>().HaveMaxLength(30);
        configurationBuilder.Properties<ImportItemStatus>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryCard>(e =>
        {
            e.Property(x => x.CardSet).HasMaxLength(200);
            e.Property(x => x.PlayerName).HasMaxLength(200);
            e.Property(x => x.CardNumber).HasMaxLength(50);
            e.Property(x => x.Parallel).HasMaxLength(200);
            e.Property(x => x.NormalizedKey).HasMaxLength(700);
            e.Property(x => x.Team).HasMaxLength(100);
            e.Property(x => x.EbayTitle).HasMaxLength(80);

            e.HasIndex(x => x.NormalizedKey).IsUnique();
            e.HasIndex(x => x.CardSet);
            e.HasIndex(x => x.PlayerName);
            e.HasIndex(x => x.Bucket);
            e.HasIndex(x => x.SgcCandidate);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ImportBatch>(e =>
        {
            e.Property(x => x.SourceFileName).HasMaxLength(260);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => x.CreatedUtc);

            e.HasMany(x => x.Items)
                .WithOne(x => x.ImportBatch)
                .HasForeignKey(x => x.ImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImportBatchItem>(e =>
        {
            e.Property(x => x.CardSet).HasMaxLength(200);
            e.Property(x => x.PlayerName).HasMaxLength(200);
            e.Property(x => x.CardNumber).HasMaxLength(50);
            e.Property(x => x.Parallel).HasMaxLength(200);

            // Deleting an inventory card keeps the history line, just unlinked.
            e.HasOne(x => x.InventoryCard)
                .WithMany(x => x.ImportItems)
                .HasForeignKey(x => x.InventoryCardId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
