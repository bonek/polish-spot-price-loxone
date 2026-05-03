namespace PolishSpotPriceToLoxone.Models;

public sealed record RefreshResult(bool Refreshed, string State, int PriceCount, DateTimeOffset NextAttemptUtc);
