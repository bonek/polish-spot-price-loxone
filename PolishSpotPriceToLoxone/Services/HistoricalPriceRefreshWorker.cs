namespace PolishSpotPriceToLoxone.Services;

public sealed class HistoricalPriceRefreshWorker(HistoricalPriceCache historicalCache, ILogger<HistoricalPriceRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshRecentExchangePrices(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshRecentExchangePrices(stoppingToken);
        }
    }

    private async Task RefreshRecentExchangePrices(CancellationToken stoppingToken)
    {
        try
        {
            var nowLocal = DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset());
            var today = DateOnly.FromDateTime(nowLocal.Date);
            await historicalCache.RefreshRangeFromExchangeAsync(today.AddDays(-1), today, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Historical exchange price refresh failed.");
        }
    }
}
