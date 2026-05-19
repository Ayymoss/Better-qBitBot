using Microsoft.EntityFrameworkCore;
using qBitBotNew.Persistence.Entities;

namespace qBitBotNew.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<FeedbackEntry> Feedback => Set<FeedbackEntry>();
    public DbSet<ResponseCache> ResponseCache => Set<ResponseCache>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite EF provider can't translate ulong comparisons in LINQ. Discord snowflakes fit in
        // 63 bits comfortably (smallest one is from 2015 and they're sequential), so round-trip as
        // long. SQLite INTEGER stores either bit-pattern identically — no data migration needed.
        configurationBuilder.Properties<ulong>().HaveConversion<long>();
        configurationBuilder.Properties<ulong?>().HaveConversion<long?>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeedbackEntry>(b =>
        {
            b.HasIndex(f => f.BotMessageId).IsUnique();
            b.HasIndex(f => f.CreatedAt);
            b.HasIndex(f => f.Helpful);
            b.Property(f => f.Intent).HasMaxLength(32);
            b.Property(f => f.Confidence).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<ResponseCache>(b =>
        {
            b.HasIndex(c => c.CreatedAt);
            b.HasIndex(c => c.BotMessageId);
        });
    }
}
