using System.Text.Json;
using Microsoft.Extensions.Options;
using PolishSpotPriceToLoxone.Models;
using PolishSpotPriceToLoxone.Options;

namespace PolishSpotPriceToLoxone.Services;

public sealed class HistoricalPriceCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly TgeRdnClient _client;
    private readonly AzureSqlPriceStore _sqlStore;
    private readonly PriceOptions _options;
    private readonly ILogger<HistoricalPriceCache> _logger;
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _sqlSeeded;
    private Dictionary<string, IReadOnlyList<HourlyPrice>> _pricesByDate = new(StringComparer.Ordinal);

    public HistoricalPriceCache(
        TgeRdnClient client,
        AzureSqlPriceStore sqlStore,
        IOptions<PriceOptions> options,
        ILogger<HistoricalPriceCache> logger)
    {
        _client = client;
        _sqlStore = sqlStore;
        _options = options.Value;
        _logger = logger;
        _cacheFile = CacheFilePaths.WritablePath(_options.HistoricalCacheFile);
        LoadFromDisk();
    }

    public async Task<IReadOnlyList<HourlyPrice>> GetPricesAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var key = date.ToString("yyyy-MM-dd");
        if (_pricesByDate.TryGetValue(key, out var cached) && cached.Count > 0)
        {
            return cached;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_pricesByDate.TryGetValue(key, out cached) && cached.Count > 0)
            {
                return cached;
            }

            var storedPrices = await _sqlStore.GetPricesAsync(date, cancellationToken);
            if (storedPrices.Count > 0)
            {
                _pricesByDate[key] = storedPrices;
                return storedPrices;
            }

            try
            {
                var prices = await _client.GetHistoricalHourlyPricesAsync(date, cancellationToken);
                _pricesByDate[key] = prices;
                await _sqlStore.SavePricesAsync(date, prices, cancellationToken);
                SaveToDisk();
                return prices;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not fetch historical prices for {Date}.", key);
                return Array.Empty<HourlyPrice>();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HistoricalRefreshResult> RefreshRangeAsync(DateOnly dateFrom, DateOnly dateTo, CancellationToken cancellationToken)
    {
        if (dateTo < dateFrom)
        {
            throw new ArgumentException("date_to must be greater than or equal to date_from");
        }

        var refreshed = 0;
        var skipped = 0;
        var totalPrices = 0;
        var errors = new List<string>();

        for (var date = dateFrom; date <= dateTo; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = date.ToString("yyyy-MM-dd");
            if (_pricesByDate.TryGetValue(key, out var cached) && cached.Count >= 23)
            {
                skipped++;
                totalPrices += cached.Count;
                continue;
            }

            try
            {
                var prices = await _client.GetHistoricalHourlyPricesAsync(date, cancellationToken);
                _pricesByDate[key] = prices;
                refreshed++;
                totalPrices += prices.Count;
                await _sqlStore.SavePricesAsync(date, prices, cancellationToken);
                SaveToDisk();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{key}: {ex.Message}");
                _logger.LogWarning(ex, "Could not refresh historical prices for {Date}.", key);
            }
        }

        return new HistoricalRefreshResult(dateFrom, dateTo, refreshed, skipped, totalPrices, errors);
    }

    public async Task<HistoricalRefreshResult> RefreshRangeFromExchangeAsync(DateOnly dateFrom, DateOnly dateTo, CancellationToken cancellationToken)
    {
        if (dateTo < dateFrom)
        {
            throw new ArgumentException("date_to must be greater than or equal to date_from");
        }

        var refreshed = 0;
        var skipped = 0;
        var totalPrices = 0;
        var errors = new List<string>();

        for (var date = dateFrom; date <= dateTo; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var prices = await _client.GetHourlyPricesAsync([date.ToDateTime(TimeOnly.MinValue)], cancellationToken);
                if (prices.Count == 0)
                {
                    skipped++;
                    continue;
                }

                await SavePricesAsync(date, prices, cancellationToken);
                refreshed++;
                totalPrices += prices.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var key = date.ToString("yyyy-MM-dd");
                errors.Add($"{key}: {ex.Message}");
                _logger.LogWarning(ex, "Could not refresh exchange prices for {Date}.", key);
            }
        }

        return new HistoricalRefreshResult(dateFrom, dateTo, refreshed, skipped, totalPrices, errors);
    }

    public async Task SavePricesAsync(DateOnly date, IReadOnlyList<HourlyPrice> prices, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _pricesByDate[date.ToString("yyyy-MM-dd")] = prices
                .Where(price => DateOnly.FromDateTime(price.HourLocal.Date) == date)
                .OrderBy(price => price.HourLocal)
                .ToArray();
            await _sqlStore.SavePricesAsync(date, _pricesByDate[date.ToString("yyyy-MM-dd")], cancellationToken);
            SaveToDisk();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadFromDisk()
    {
        foreach (var cacheFile in CacheFilePaths.ReadCandidates(_options.HistoricalCacheFile, _cacheFile)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Reverse()
                     .Where(File.Exists))
        {
            try
            {
                var json = File.ReadAllText(cacheFile);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<HourlyPrice>>>(json, JsonOptions);
                if (loaded is null)
                {
                    continue;
                }

                foreach (var item in loaded)
                {
                    _pricesByDate[item.Key] = item.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read historical price cache file {CacheFile}.", cacheFile);
            }
        }
    }

    public Task SeedSqlAsync(CancellationToken cancellationToken)
    {
        if (_sqlSeeded)
        {
            return Task.CompletedTask;
        }

        _sqlSeeded = true;
        return _sqlStore.SeedAsync(_pricesByDate, cancellationToken);
    }

    private void SaveToDisk()
    {
        var directory = Path.GetDirectoryName(_cacheFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_pricesByDate, JsonOptions));
    }
}

public sealed record HistoricalRefreshResult(
    DateOnly DateFrom,
    DateOnly DateTo,
    int RefreshedDays,
    int SkippedDays,
    int TotalPrices,
    IReadOnlyList<string> Errors);
