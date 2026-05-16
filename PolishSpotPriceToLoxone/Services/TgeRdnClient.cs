using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PolishSpotPriceToLoxone.Models;
using PolishSpotPriceToLoxone.Options;

namespace PolishSpotPriceToLoxone.Services;

public sealed partial class TgeRdnClient(HttpClient httpClient, IOptions<PriceOptions> options)
{
    private readonly PriceOptions _options = options.Value;

    public async Task<IReadOnlyList<HourlyPrice>> GetHourlyPricesAsync(IEnumerable<DateTime> dates, CancellationToken cancellationToken)
    {
        var allPrices = new List<HourlyPrice>();
        foreach (var date in dates.Select(DateOnly.FromDateTime).Distinct())
        {
            var prices = await GetHourlyPricesForDeliveryDateAsync(date, cancellationToken);
            allPrices.AddRange(prices);
        }

        return allPrices
            .OrderBy(price => price.HourLocal)
            .ToArray();
    }

    public async Task<IReadOnlyList<HourlyPrice>> GetHistoricalHourlyPricesAsync(DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        var dateFrom = Uri.EscapeDataString($"{deliveryDate:yyyy-MM-dd} 00:00:00");
        var dateTo = Uri.EscapeDataString($"{deliveryDate.AddDays(1):yyyy-MM-dd} 00:00:00");
        var url = $"{_options.HistoricalTgeApiUrl}?source=TGE&contract=Fix_1&date_from={dateFrom}&date_to={dateTo}&limit=100";

        using var stream = await httpClient.GetStreamAsync(url, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var values = document.RootElement;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            (!document.RootElement.TryGetProperty("value", out values) || values.ValueKind != JsonValueKind.Array))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var prices = new List<HourlyPrice>();
        foreach (var item in values.EnumerateArray())
        {
            var price = TryMapHistoricalHourly(item, deliveryDate);
            if (price is not null)
            {
                prices.Add(price);
            }
        }

        return prices
            .OrderBy(price => price.HourLocal)
            .ToArray();
    }

    private async Task<IReadOnlyList<HourlyPrice>> GetHourlyPricesForDeliveryDateAsync(DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        var sessionDate = deliveryDate.AddDays(-1);
        var url = $"{_options.TgeUrl}?dateShow={sessionDate:dd-MM-yyyy}";
        var html = await httpClient.GetStringAsync(url, cancellationToken);

        var dateOfData = TryGetDateOfHourlyData(html);
        if (dateOfData is not null && dateOfData.Value != deliveryDate)
        {
            return [];
        }

        var tablePrices = ExtractHourlyTableRows(html)
            .Select(row => TryMapHourly(row, deliveryDate, _options.MarketColumn))
            .OfType<HourlyPrice>()
            .ToArray();

        if (tablePrices.Length > 0)
        {
            return tablePrices;
        }

        return ExtractHourlyInstrumentRows(html, deliveryDate, _options.MarketColumn)
            .ToArray();
    }

    private static DateOnly? TryGetDateOfHourlyData(string html)
    {
        foreach (Match match in ContractDateRegex().Matches(html))
        {
            var text = HtmlToText(match.Groups["content"].Value);
            if (!text.Contains("godzinowe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dateMatch = DateRegex().Match(text);
            if (dateMatch.Success &&
                DateOnly.TryParseExact(dateMatch.Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        var pageText = HtmlToText(html);
        var titleMatch = DeliveryDateTitleRegex().Match(pageText);
        if (titleMatch.Success &&
            DateOnly.TryParseExact(titleMatch.Groups["date"].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var titleDate))
        {
            return titleDate;
        }

        return null;
    }

    private static IEnumerable<IReadOnlyList<string>> ExtractHourlyTableRows(string html)
    {
        var table = HourlyTableRegex().Match(html);
        if (!table.Success)
        {
            yield break;
        }

        foreach (Match row in TableRowRegex().Matches(table.Groups["body"].Value))
        {
            var cells = TableCellRegex().Matches(row.Groups["row"].Value)
                .Select(cell => HtmlToText(cell.Groups["cell"].Value))
                .ToArray();

            if (cells.Length > 0 && HourRangeRegex().IsMatch(cells[0]))
            {
                yield return cells;
            }
        }
    }

    private static HourlyPrice? TryMapHourly(IReadOnlyList<string> cells, DateOnly deliveryDate, string marketColumn)
    {
        if (!TryGetHourStart(cells[0], out var hour))
        {
            return null;
        }

        var priceColumn = marketColumn.Equals("fixing2", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        if (cells.Count <= priceColumn || !TryParsePlnPerMwh(cells[priceColumn], out var price))
        {
            return null;
        }

        var date = deliveryDate.ToDateTime(TimeOnly.MinValue);
        var hourStart = date.AddHours(hour);
        return new HourlyPrice(new DateTimeOffset(hourStart, WarsawOffsetFor(hourStart)), price, DateTime.Now);
    }

    private static IEnumerable<HourlyPrice> ExtractHourlyInstrumentRows(string html, DateOnly deliveryDate, string marketColumn)
    {
        var text = HtmlToText(html);
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < tokens.Length - 3; index++)
        {
            var instrument = InstrumentRegex().Match(tokens[index]);
            if (!instrument.Success || tokens[index + 1] != "60")
            {
                continue;
            }

            if (!DateOnly.TryParseExact(instrument.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rowDate) ||
                rowDate != deliveryDate ||
                !int.TryParse(instrument.Groups["hour"].Value, CultureInfo.InvariantCulture, out var tgeHour))
            {
                continue;
            }

            var priceIndex = marketColumn.Equals("fixing2", StringComparison.OrdinalIgnoreCase) ? 5 : 2;
            if (tokens.Length <= index + priceIndex || !TryParsePlnPerMwh(tokens[index + priceIndex], out var price))
            {
                continue;
            }

            var date = deliveryDate.ToDateTime(TimeOnly.MinValue);
            var hourStart = date.AddHours(tgeHour - 1);
            yield return new HourlyPrice(new DateTimeOffset(hourStart, WarsawOffsetFor(hourStart)), price, DateTime.Now);
        }
    }

    private static HourlyPrice? TryMapHistoricalHourly(JsonElement item, DateOnly deliveryDate)
    {
        if (!item.TryGetProperty("date_time", out var dateTimeElement) ||
            !DateTime.TryParseExact(dateTimeElement.GetString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hourStart) ||
            DateOnly.FromDateTime(hourStart) != deliveryDate ||
            !item.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (!attribute.TryGetProperty("name", out var name) ||
                !string.Equals(name.GetString(), "price", StringComparison.OrdinalIgnoreCase) ||
                !attribute.TryGetProperty("value", out var value) ||
                !TryParsePlnPerMwh(value.GetString() ?? string.Empty, out var price))
            {
                continue;
            }

            return new HourlyPrice(new DateTimeOffset(hourStart, WarsawOffsetFor(hourStart)), price, DateTime.Now);
        }

        return null;
    }

    private static bool TryGetHourStart(string value, out int hour)
    {
        var match = HourRangeRegex().Match(value);
        return int.TryParse(match.Groups["from"].Value, CultureInfo.InvariantCulture, out hour);
    }

    private static bool TryParsePlnPerMwh(string value, out decimal price)
    {
        value = value.Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
    }

    private static string HtmlToText(string html)
    {
        var withoutTags = Regex.Replace(html, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ");
    }

    private static TimeSpan WarsawOffsetFor(DateTime dateTime)
    {
        return PriceCache.GetWarsawTimeZone().GetUtcOffset(dateTime);
    }

    [GeneratedRegex(@"<[^>]*class\s*=\s*[""'][^""']*\bkontrakt-date\b[^""']*[""'][^>]*>(?<content>.*?)</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ContractDateRegex();

    [GeneratedRegex(@"\d{2}-\d{2}-\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"Kontrakty dla dostawy w dniu (?<date>\d{2}-\d{2}-\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeliveryDateTitleRegex();

    [GeneratedRegex(@"<table[^>]*id\s*=\s*[""']footable_kontrakty_godzinowe[""'][^>]*>.*?<tbody[^>]*>(?<body>.*?)</tbody>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HourlyTableRegex();

    [GeneratedRegex(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<td[^>]*>(?<cell>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TableCellRegex();

    [GeneratedRegex(@"^(?<from>\d{1,2})\s*-\s*\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex HourRangeRegex();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})_H(?<hour>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex InstrumentRegex();
}
