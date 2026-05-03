namespace PolishSpotPriceToLoxone.Models;

public sealed record PriceSnapshot(
    DateTimeOffset? LastSuccessfulRefreshUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset NextAttemptUtc,
    IReadOnlyList<HourlyPrice> Prices,
    string State,
    string? LastError)
{
    public bool HasAnyPrices => Prices.Count > 0;
    public int PriceCount => Prices.Count;
    public IReadOnlyList<string> Dates => Prices.Select(price => price.HourLocal.ToString("yyyy-MM-dd")).Distinct().ToArray();

    public static PriceSnapshot Empty() => new(null, null, DateTimeOffset.MinValue, Array.Empty<HourlyPrice>(), "empty", null);
}
