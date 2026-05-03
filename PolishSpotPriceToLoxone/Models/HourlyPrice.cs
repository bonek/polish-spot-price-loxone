namespace PolishSpotPriceToLoxone.Models;

public sealed record HourlyPrice(DateTimeOffset HourLocal, decimal PricePlnPerMwh, DateTime PublicationTimeLocal)
{
    public decimal PricePlnPerKwh => Math.Round(PricePlnPerMwh / 1000m, 5, MidpointRounding.AwayFromZero);
}
