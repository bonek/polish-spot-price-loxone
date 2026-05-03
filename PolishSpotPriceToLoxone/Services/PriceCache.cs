using System.Text.Json;
using Microsoft.Extensions.Options;
using PolishSpotPriceToLoxone.Models;
using PolishSpotPriceToLoxone.Options;

namespace PolishSpotPriceToLoxone.Services;

public sealed class PriceCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly TgeRdnClient _client;
    private readonly PriceOptions _options;
    private readonly ILogger<PriceCache> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private PriceSnapshot _snapshot = PriceSnapshot.Empty();

    public PriceCache(TgeRdnClient client, IOptions<PriceOptions> options, ILogger<PriceCache> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        LoadFromDisk();
        _snapshot = _snapshot with { NextAttemptUtc = CalculateNextAttempt(DateTimeOffset.UtcNow) };
    }

    public static TimeSpan WarsawOffset()
    {
        return GetWarsawTimeZone().GetUtcOffset(DateTimeOffset.UtcNow);
    }

    public static TimeZoneInfo GetWarsawTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        }
    }

    public PriceSnapshot GetSnapshot() => _snapshot;

    public bool TryGetPrice(DateTimeOffset targetHour, string? unit, out decimal price)
    {
        var hourStart = new DateTimeOffset(targetHour.Year, targetHour.Month, targetHour.Day, targetHour.Hour, 0, 0, targetHour.Offset);
        var item = _snapshot.Prices.FirstOrDefault(price => price.HourLocal == hourStart);
        if (item is null)
        {
            price = 0;
            return false;
        }

        price = ConvertUnit(item.PricePlnPerMwh, unit);
        return true;
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_snapshot.HasAnyPrices || _snapshot.NextAttemptUtc <= now)
        {
            await RefreshAsync(force: false, cancellationToken);
        }
    }

    public async Task<RefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (!force && _snapshot.HasAnyPrices && _snapshot.NextAttemptUtc > nowUtc)
            {
                return new RefreshResult(false, "refresh-not-due", _snapshot.PriceCount, _snapshot.NextAttemptUtc);
            }

            var nowLocal = nowUtc.ToOffset(WarsawOffset());
            var dates = new[] { nowLocal.Date, nowLocal.Date.AddDays(1) };

            try
            {
                var prices = await _client.GetHourlyPricesAsync(dates, cancellationToken);
                var expectedTomorrow = nowLocal.TimeOfDay >= new TimeSpan(_options.RefreshStartHour, _options.RefreshStartMinute, 0);
                var tomorrow = DateOnly.FromDateTime(nowLocal.Date.AddDays(1));
                var hasTomorrow = prices.Any(price => DateOnly.FromDateTime(price.HourLocal.Date) == tomorrow);
                var completeEnough = !expectedTomorrow || prices.Count(price => DateOnly.FromDateTime(price.HourLocal.Date) == tomorrow) >= 23;
                var nextAttempt = completeEnough
                    ? CalculateNextAttempt(nowUtc.AddMinutes(_options.RegularRefreshMinutes))
                    : nowUtc.AddMinutes(_options.RetryMinutes);

                _snapshot = new PriceSnapshot(
                    nowUtc,
                    nowUtc,
                    nextAttempt,
                    prices,
                    hasTomorrow ? "ok" : "waiting-for-tomorrow-prices",
                    null);
                SaveToDisk();

                return new RefreshResult(true, _snapshot.State, _snapshot.PriceCount, _snapshot.NextAttemptUtc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to refresh TGE RDN prices.");
                _snapshot = _snapshot with
                {
                    LastAttemptUtc = nowUtc,
                    NextAttemptUtc = nowUtc.AddMinutes(_options.RetryMinutes),
                    State = "refresh-failed",
                    LastError = ex.Message
                };
                SaveToDisk();
                return new RefreshResult(false, _snapshot.State, _snapshot.PriceCount, _snapshot.NextAttemptUtc);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private decimal ConvertUnit(decimal plnPerMwh, string? unit)
    {
        var effectiveUnit = string.IsNullOrWhiteSpace(unit) ? _options.DefaultUnit : unit;
        return effectiveUnit.Equals("mwh", StringComparison.OrdinalIgnoreCase)
            ? plnPerMwh
            : Math.Round(plnPerMwh / 1000m, 5, MidpointRounding.AwayFromZero);
    }

    private DateTimeOffset CalculateNextAttempt(DateTimeOffset fromUtc)
    {
        var local = fromUtc.ToOffset(WarsawOffset());
        var todayRefresh = new DateTimeOffset(local.Year, local.Month, local.Day, _options.RefreshStartHour, _options.RefreshStartMinute, 0, local.Offset);
        var nextLocal = local <= todayRefresh ? todayRefresh : todayRefresh.AddDays(1);

        if (local.Hour >= _options.RefreshStartHour && local.Hour < _options.RefreshEndHour)
        {
            nextLocal = local.AddMinutes(_options.RetryMinutes);
        }

        return nextLocal.ToUniversalTime();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_options.CacheFile))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_options.CacheFile);
            _snapshot = JsonSerializer.Deserialize<PriceSnapshot>(json, JsonOptions) ?? PriceSnapshot.Empty();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read price cache file.");
        }
    }

    private void SaveToDisk()
    {
        var directory = Path.GetDirectoryName(_options.CacheFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_options.CacheFile, JsonSerializer.Serialize(_snapshot, JsonOptions));
    }
}
