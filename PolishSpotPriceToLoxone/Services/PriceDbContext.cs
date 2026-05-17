using Microsoft.EntityFrameworkCore;

namespace PolishSpotPriceToLoxone.Services;

public sealed class PriceDbContext(DbContextOptions<PriceDbContext> options) : DbContext(options)
{
    public DbSet<PriceDbRecord> Prices => Set<PriceDbRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceDbRecord>(entity =>
        {
            entity.ToTable("Prices");
            entity.HasKey(price => new { price.Date, price.Hour });
            entity.HasIndex(price => price.Date);
            entity.Property(price => price.PricePlnPerMwh).HasPrecision(18, 2);
            entity.Property(price => price.PricePlnPerKwh).HasPrecision(18, 5);
            entity.Property(price => price.Source).HasMaxLength(32);
        });
    }
}

public sealed class PriceDbRecord
{
    public DateOnly Date { get; set; }
    public int Hour { get; set; }
    public DateTimeOffset HourLocal { get; set; }
    public decimal PricePlnPerMwh { get; set; }
    public decimal PricePlnPerKwh { get; set; }
    public DateTime PublicationTimeLocal { get; set; }
    public string Source { get; set; } = "TGE";
}
