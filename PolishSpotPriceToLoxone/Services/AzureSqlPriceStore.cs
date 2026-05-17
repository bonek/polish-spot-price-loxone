using Microsoft.EntityFrameworkCore;
using PolishSpotPriceToLoxone.Models;

namespace PolishSpotPriceToLoxone.Services;

public sealed class AzureSqlPriceStore
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureSqlPriceStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public AzureSqlPriceStore(IConfiguration configuration, ILogger<AzureSqlPriceStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(GetConnectionString());

    public async Task<IReadOnlyList<HourlyPrice>> GetPricesAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return Array.Empty<HourlyPrice>();
        }

        try
        {
            await using var db = await CreateDbContextAsync(cancellationToken);
            var records = await db.Prices
                .AsNoTracking()
                .Where(price => price.Date == date)
                .OrderBy(price => price.Hour)
                .ToArrayAsync(cancellationToken);

            return records
                .Select(record => new HourlyPrice(record.HourLocal, record.PricePlnPerMwh, record.PublicationTimeLocal))
                .ToArray();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read prices from Azure SQL for {Date}.", date);
            return Array.Empty<HourlyPrice>();
        }
    }

    public async Task SavePricesAsync(DateOnly date, IReadOnlyList<HourlyPrice> prices, CancellationToken cancellationToken)
    {
        if (!IsEnabled || prices.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await CreateDbContextAsync(cancellationToken);
            var existing = db.Prices.Where(price => price.Date == date);
            db.Prices.RemoveRange(existing);
            await db.Prices.AddRangeAsync(ToRecords(date, prices), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not save prices to Azure SQL for {Date}.", date);
        }
    }

    public async Task SeedAsync(IReadOnlyDictionary<string, IReadOnlyList<HourlyPrice>> pricesByDate, CancellationToken cancellationToken)
    {
        if (!IsEnabled || pricesByDate.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await CreateDbContextAsync(cancellationToken);
            var existingDates = await db.Prices
                .AsNoTracking()
                .Select(price => price.Date)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var existing = existingDates.ToHashSet();

            foreach (var item in pricesByDate)
            {
                if (!DateOnly.TryParse(item.Key, out var date) || existing.Contains(date) || item.Value.Count == 0)
                {
                    continue;
                }

                await db.Prices.AddRangeAsync(ToRecords(date, item.Value), cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not seed Azure SQL prices.");
        }
    }

    private async Task<PriceDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<PriceDbContext>()
            .UseSqlServer(GetConnectionString())
            .Options;
        return new PriceDbContext(options);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var options = new DbContextOptionsBuilder<PriceDbContext>()
                .UseSqlServer(GetConnectionString())
                .Options;
            await using var db = new PriceDbContext(options);
            await db.Database.EnsureCreatedAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private string? GetConnectionString()
    {
        return _configuration.GetConnectionString("PricesSql") ??
               Environment.GetEnvironmentVariable("SQLAZURECONNSTR_PricesSql") ??
               Environment.GetEnvironmentVariable("SQLCONNSTR_PricesSql") ??
               _configuration["PricesSqlConnectionString"];
    }

    private static IEnumerable<PriceDbRecord> ToRecords(DateOnly date, IReadOnlyList<HourlyPrice> prices)
    {
        return prices
            .Where(price => DateOnly.FromDateTime(price.HourLocal.Date) == date)
            .OrderBy(price => price.HourLocal)
            .Select(price => new PriceDbRecord
            {
                Date = date,
                Hour = price.HourLocal.Hour,
                HourLocal = price.HourLocal,
                PricePlnPerMwh = price.PricePlnPerMwh,
                PricePlnPerKwh = price.PricePlnPerKwh,
                PublicationTimeLocal = price.PublicationTimeLocal,
                Source = "TGE"
            });
    }
}
