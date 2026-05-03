namespace PolishSpotPriceToLoxone.Services;

public sealed class PriceRefreshWorker(PriceCache cache, ILogger<PriceRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await cache.RefreshAsync(force: false, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = cache.GetSnapshot();
            var delay = snapshot.NextAttemptUtc - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromMinutes(1))
            {
                delay = TimeSpan.FromMinutes(1);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
                await cache.RefreshAsync(force: false, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unexpected background refresh failure.");
            }
        }
    }
}
