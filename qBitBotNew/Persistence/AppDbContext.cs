using Microsoft.EntityFrameworkCore;
using qBitBotNew.Persistence.Entities;

namespace qBitBotNew.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<FeedbackEntry> Feedback => Set<FeedbackEntry>();
    public DbSet<ResponseCache> ResponseCache => Set<ResponseCache>();
    public DbSet<GreetSuppression> GreetSuppressions => Set<GreetSuppression>();

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

        modelBuilder.Entity<GreetSuppression>(b =>
        {
            b.HasKey(g => g.UserId);
            b.Property(g => g.UserId).ValueGeneratedNever();
            b.Property(g => g.Reason).HasConversion<string>().HasMaxLength(32);
        });
    }
}
