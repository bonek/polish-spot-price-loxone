namespace PolishSpotPriceToLoxone.Options;

public sealed class PriceOptions
{
    public const string SectionName = "Prices";

    public string TgeUrl { get; init; } = "https://tge.pl/energia-elektryczna-rdn";
    public string CacheFile { get; init; } = "data/prices-cache.json";
    public string DefaultUnit { get; init; } = "kwh";
    public string MarketColumn { get; init; } = "fixing1";
    public int RefreshStartHour { get; init; } = 10;
    public int RefreshStartMinute { get; init; } = 35;
    public int RefreshEndHour { get; init; } = 14;
    public int RetryMinutes { get; init; } = 15;
    public int RegularRefreshMinutes { get; init; } = 60;
}
