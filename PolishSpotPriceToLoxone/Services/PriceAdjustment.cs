namespace PolishSpotPriceToLoxone.Services;

public sealed record PriceAdjustment(bool Pstryk, string? Distributor, string? Tariff, bool IncludeVat)
{
    private const decimal PstrykMarginPlnPerKwh = 0.08m;
    private const decimal ExcisePlnPerKwh = 0.005m;
    private const decimal QualityPlnPerKwh = 0.0332m;
    private const decimal OzePlnPerKwh = 0.0073m;
    private const decimal CogenerationPlnPerKwh = 0.0030m;
    private const decimal VatMultiplier = 1.23m;

    private static readonly Dictionary<string, DistributorTariffs> Distributors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tauron"] = new(
            ["tauron", "tauron-dystrybucja", "tauron_dystrybucja"],
            new Dictionary<string, TariffRates>(StringComparer.OrdinalIgnoreCase)
            {
                ["g11"] = TariffRates.SingleZone(0.2464m),
                ["g12"] = TariffRates.TwoZone(0.2841m, 0.0558m, ZoneRule.G12),
                ["g12w"] = TariffRates.TwoZone(0.3298m, 0.0512m, ZoneRule.G12Weekend),
                ["g13"] = TariffRates.ThreeZone(0.2203m, 0.3898m, 0.0392m, ZoneRule.G13),
                ["g13s"] = TariffRates.G13S(0.2842m, 0.1000m, 0.1094m, 0.1176m, 0.0400m, 0.1094m)
            }),
        ["energa"] = new(
            ["energa", "energa-operator", "energa_operator"],
            new Dictionary<string, TariffRates>(StringComparer.OrdinalIgnoreCase)
            {
                ["g11"] = TariffRates.SingleZone(0.3485m),
                ["g11f"] = TariffRates.SingleZone(0.0516m),
                ["g12"] = TariffRates.TwoZone(0.3844m, 0.0827m, ZoneRule.G12),
                ["g12r"] = TariffRates.TwoZone(0.3640m, 0.0882m, ZoneRule.G12R),
                ["g12w"] = TariffRates.TwoZone(0.4017m, 0.0851m, ZoneRule.G12Weekend)
            }),
        ["pge"] = new(
            ["pge", "pge-dystrybucja", "pge_dystrybucja"],
            new Dictionary<string, TariffRates>(StringComparer.OrdinalIgnoreCase)
            {
                ["g11"] = TariffRates.SingleZone(0.3469m),
                ["g12"] = TariffRates.TwoZone(0.4014m, 0.0765m, ZoneRule.NightOnly),
                ["g12e"] = TariffRates.TwoZone(0.3851m, 0.0349m, ZoneRule.G12Weekend),
                ["g12n"] = TariffRates.TwoZone(0.3470m, 0.0347m, ZoneRule.G12Weekend),
                ["g12w"] = TariffRates.TwoZone(0.4276m, 0.0845m, ZoneRule.G12Weekend)
            }),
        ["enea"] = new(
            ["enea", "enea-operator", "enea_operator"],
            new Dictionary<string, TariffRates>(StringComparer.OrdinalIgnoreCase)
            {
                ["g11"] = TariffRates.SingleZone(0.2456m),
                ["g12"] = TariffRates.TwoZone(0.2779m, 0.0913m, ZoneRule.G12),
                ["g12sezon"] = TariffRates.TwoZone(0.2779m, 0.0913m, ZoneRule.G12Season),
                ["g12w"] = TariffRates.TwoZone(0.2702m, 0.0813m, ZoneRule.G12Weekend),
                ["g13active"] = TariffRates.ThreeZone(0.2456m, 0.3032m, 0.0730m, ZoneRule.G13Active)
            }),
        ["stoen"] = new(
            ["stoen", "eon", "e-on", "e.on", "stoen-operator", "stoen_operator"],
            new Dictionary<string, TariffRates>(StringComparer.OrdinalIgnoreCase)
            {
                ["g11"] = TariffRates.SingleZone(0.2342m),
                ["g12"] = TariffRates.TwoZone(0.2545m, 0.0555m, ZoneRule.G12),
                ["g12w"] = TariffRates.TwoZone(0.2570m, 0.1079m, ZoneRule.G12Weekend)
            })
    };

    private static readonly Dictionary<string, string> DistributorAliases = Distributors
        .SelectMany(distributor => distributor.Value.Aliases.Select(alias => new { Alias = alias, Canonical = distributor.Key }))
        .ToDictionary(item => item.Alias, item => item.Canonical, StringComparer.OrdinalIgnoreCase);

    public static bool TryCreate(
        bool? pstryk,
        string? seller,
        string? distributor,
        string? distribution,
        string? tariff,
        bool? vat,
        bool? gross,
        bool? brutto,
        out PriceAdjustment adjustment,
        out string? error)
    {
        var effectiveDistributor = FirstNonBlank(distributor, distribution);
        var hasDistributor = !string.IsNullOrWhiteSpace(effectiveDistributor);
        var hasTariff = !string.IsNullOrWhiteSpace(tariff);

        if (!hasDistributor && hasTariff)
        {
            effectiveDistributor = "tauron";
            hasDistributor = true;
        }

        if (hasDistributor && !hasTariff)
        {
            adjustment = Empty;
            error = "tariff is required when distributor is provided";
            return false;
        }

        var canonicalDistributor = (string?)null;
        if (hasDistributor && !DistributorAliases.TryGetValue(effectiveDistributor!, out canonicalDistributor))
        {
            adjustment = Empty;
            error = $"distributor must be one of: {string.Join(", ", Distributors.Keys)}";
            return false;
        }

        var effectiveTariff = string.IsNullOrWhiteSpace(tariff) ? null : tariff.Trim().ToLowerInvariant();
        if (canonicalDistributor is not null && effectiveTariff is not null)
        {
            var tariffs = Distributors[canonicalDistributor];
            if (tariffs.AliasesByTariff.TryGetValue(effectiveTariff, out var tariffAlias))
            {
                effectiveTariff = tariffAlias;
            }

            if (!tariffs.RatesByTariff.ContainsKey(effectiveTariff))
            {
                adjustment = Empty;
                error = $"tariff for {canonicalDistributor} must be one of: {string.Join(", ", tariffs.SupportedTariffs)}";
                return false;
            }
        }

        var usePstrykMargin = pstryk == true;
        if (!string.IsNullOrWhiteSpace(seller))
        {
            if (!seller.Equals("pstryk", StringComparison.OrdinalIgnoreCase))
            {
                adjustment = Empty;
                error = "seller must be pstryk or omitted";
                return false;
            }

            usePstrykMargin = true;
        }

        adjustment = new PriceAdjustment(
            usePstrykMargin,
            canonicalDistributor,
            effectiveTariff,
            effectiveTariff is not null || vat == true || gross == true || brutto == true);
        error = null;
        return true;
    }

    public static PriceAdjustment Empty { get; } = new(false, null, null, false);

    public decimal Apply(DateTimeOffset hourLocal, decimal basePrice, string? unit)
    {
        var effectiveUnit = string.IsNullOrWhiteSpace(unit) ? "kwh" : unit.Trim();
        var isMwh = effectiveUnit.Equals("mwh", StringComparison.OrdinalIgnoreCase);

        var hasDistribution = Distributor is not null && Tariff is not null;
        if (hasDistribution)
        {
            var energyPlnPerKwh = isMwh ? basePrice / 1000m : basePrice;
            var roundedEnergyPlnPerKwh = Math.Round(energyPlnPerKwh, 2, MidpointRounding.AwayFromZero);
            var netPlnPerKwh = roundedEnergyPlnPerKwh +
                               GetVariableDistributionPlnPerKwh(hourLocal, Distributor!, Tariff!) +
                               QualityPlnPerKwh +
                               OzePlnPerKwh +
                               CogenerationPlnPerKwh +
                               ExcisePlnPerKwh;

            if (Pstryk)
            {
                netPlnPerKwh += PstrykMarginPlnPerKwh;
            }

            var grossAdjustedPlnPerKwh = netPlnPerKwh;
            if (IncludeVat)
            {
                grossAdjustedPlnPerKwh += Math.Round(netPlnPerKwh * (VatMultiplier - 1m), 2, MidpointRounding.AwayFromZero);
            }

            return isMwh
                ? Math.Round(grossAdjustedPlnPerKwh * 1000m, 2, MidpointRounding.AwayFromZero)
                : Math.Ceiling(grossAdjustedPlnPerKwh * 100m) / 100m;
        }

        var adjustedPlnPerKwh = isMwh ? basePrice / 1000m : basePrice;
        if (Pstryk)
        {
            adjustedPlnPerKwh += PstrykMarginPlnPerKwh;
        }

        if (IncludeVat)
        {
            adjustedPlnPerKwh *= VatMultiplier;
        }

        if (isMwh)
        {
            return Math.Round(adjustedPlnPerKwh * 1000m, 2, MidpointRounding.AwayFromZero);
        }

        return Math.Round(adjustedPlnPerKwh, 2, MidpointRounding.AwayFromZero);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> SupportedTariffs()
    {
        return Distributors.ToDictionary(
            distributor => distributor.Key,
            distributor => distributor.Value.SupportedTariffs,
            StringComparer.OrdinalIgnoreCase);
    }

    private static decimal GetVariableDistributionPlnPerKwh(DateTimeOffset hourLocal, string distributor, string tariff)
    {
        var rates = Distributors[distributor].RatesByTariff[tariff];
        return rates.Rule switch
        {
            ZoneRule.Single => rates.Single!.Value,
            ZoneRule.G12 => IsG12OffPeak(hourLocal) ? rates.OffPeak!.Value : rates.Peak!.Value,
            ZoneRule.G12R => IsG12ROffPeak(hourLocal) ? rates.OffPeak!.Value : rates.Peak!.Value,
            ZoneRule.NightOnly => IsHourInRange(hourLocal.Hour, 22, 6) ? rates.OffPeak!.Value : rates.Peak!.Value,
            ZoneRule.G12Season => IsG12SeasonOffPeak(hourLocal) ? rates.OffPeak!.Value : rates.Peak!.Value,
            ZoneRule.G12Weekend => IsG12WeekendOffPeak(hourLocal) ? rates.OffPeak!.Value : rates.Peak!.Value,
            ZoneRule.G13 => GetG13NetworkRate(hourLocal, rates),
            ZoneRule.G13Active => GetG13ActiveNetworkRate(hourLocal, rates),
            ZoneRule.G13S => GetG13SNetworkRate(hourLocal, rates),
            _ => throw new InvalidOperationException($"Unsupported tariff rule '{rates.Rule}'.")
        };
    }

    private static bool IsG12OffPeak(DateTimeOffset hourLocal)
    {
        return IsHourInRange(hourLocal.Hour, 22, 6) || IsHourInRange(hourLocal.Hour, 13, 15);
    }

    private static bool IsG12WeekendOffPeak(DateTimeOffset hourLocal)
    {
        return IsPolishHolidayOrWeekend(hourLocal.Date) ||
               IsHourInRange(hourLocal.Hour, 22, 6) ||
               IsHourInRange(hourLocal.Hour, 13, 15);
    }

    private static bool IsG12ROffPeak(DateTimeOffset hourLocal)
    {
        return IsHourInRange(hourLocal.Hour, 22, 7) ||
               IsHourInRange(hourLocal.Hour, 13, 15);
    }

    private static bool IsG12SeasonOffPeak(DateTimeOffset hourLocal)
    {
        return IsHourInRange(hourLocal.Hour, 4, 6) ||
               IsHourInRange(hourLocal.Hour, 9, 17);
    }

    private static decimal GetG13NetworkRate(DateTimeOffset hourLocal, TariffRates rates)
    {
        if (IsPolishHolidayOrWeekend(hourLocal.Date))
        {
            return rates.OffPeak!.Value;
        }

        var hour = hourLocal.Hour;
        var isSummer = hourLocal.Month is >= 4 and <= 9;
        if (IsHourInRange(hour, 7, 13))
        {
            return rates.MorningPeak!.Value;
        }

        if (isSummer && IsHourInRange(hour, 19, 22))
        {
            return rates.EveningPeak!.Value;
        }

        if (!isSummer && IsHourInRange(hour, 16, 21))
        {
            return rates.EveningPeak!.Value;
        }

        return rates.OffPeak!.Value;
    }

    private static decimal GetG13ActiveNetworkRate(DateTimeOffset hourLocal, TariffRates rates)
    {
        var hour = hourLocal.Hour;
        if (IsHourInRange(hour, 9, 17))
        {
            return rates.OffPeak!.Value;
        }

        if (IsHourInRange(hour, 6, 9) || IsHourInRange(hour, 18, 23))
        {
            return rates.EveningPeak!.Value;
        }

        return rates.MorningPeak!.Value;
    }

    private static decimal GetG13SNetworkRate(DateTimeOffset hourLocal, TariffRates rates)
    {
        var hour = hourLocal.Hour;
        if (IsPolishHolidayOrWeekend(hourLocal.Date))
        {
            if (IsHourInRange(hour, 7, 9) || IsHourInRange(hour, 17, 21))
            {
                return rates.MorningPeakWeekend!.Value;
            }

            if (IsHourInRange(hour, 9, 21))
            {
                return rates.EveningPeakWeekend!.Value;
            }

            return rates.OffPeakWeekend!.Value;
        }

        if (IsHourInRange(hour, 7, 13) || IsHourInRange(hour, 17, 22))
        {
            return rates.MorningPeak!.Value;
        }

        if (IsHourInRange(hour, 13, 17))
        {
            return rates.EveningPeak!.Value;
        }

        return rates.OffPeak!.Value;
    }

    private static bool IsHourInRange(int hour, int startInclusive, int endExclusive)
    {
        return startInclusive < endExclusive
            ? hour >= startInclusive && hour < endExclusive
            : hour >= startInclusive || hour < endExclusive;
    }

    private static bool IsPolishHolidayOrWeekend(DateTime date)
    {
        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsPolishPublicHoliday(DateOnly.FromDateTime(date));
    }

    private static bool IsPolishPublicHoliday(DateOnly date)
    {
        var easter = GetEasterSunday(date.Year);
        return date is { Month: 1, Day: 1 } ||
               date is { Month: 1, Day: 6 } ||
               date == easter.AddDays(1) ||
               date is { Month: 5, Day: 1 } ||
               date is { Month: 5, Day: 3 } ||
               date == easter.AddDays(60) ||
               date is { Month: 8, Day: 15 } ||
               date is { Month: 11, Day: 1 } ||
               date is { Month: 11, Day: 11 } ||
               date is { Month: 12, Day: 25 } ||
               date is { Month: 12, Day: 26 };
    }

    private static DateOnly GetEasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private sealed record DistributorTariffs(
        IReadOnlyList<string> Aliases,
        IReadOnlyDictionary<string, TariffRates> RatesByTariff,
        IReadOnlyDictionary<string, string>? AliasesByTariff = null)
    {
        public IReadOnlyDictionary<string, string> AliasesByTariff { get; } =
            AliasesByTariff ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> SupportedTariffs => RatesByTariff.Keys.Concat(this.AliasesByTariff.Keys).OrderBy(value => value).ToArray();
    }

    private sealed record TariffRates(
        decimal? Single,
        decimal? Peak,
        decimal? OffPeak,
        decimal? MorningPeak,
        decimal? EveningPeak,
        decimal? MorningPeakWeekend,
        decimal? EveningPeakWeekend,
        decimal? OffPeakWeekend,
        ZoneRule Rule)
    {
        public static TariffRates SingleZone(decimal rate) => new(rate, null, null, null, null, null, null, null, ZoneRule.Single);

        public static TariffRates TwoZone(decimal peak, decimal offPeak, ZoneRule rule) => new(null, peak, offPeak, null, null, null, null, null, rule);

        public static TariffRates ThreeZone(decimal morningPeak, decimal eveningPeak, decimal offPeak, ZoneRule rule) =>
            new(null, null, offPeak, morningPeak, eveningPeak, null, null, null, rule);

        public static TariffRates G13S(
            decimal morningPeak,
            decimal eveningPeak,
            decimal offPeak,
            decimal morningPeakWeekend,
            decimal eveningPeakWeekend,
            decimal offPeakWeekend) =>
            new(null, null, offPeak, morningPeak, eveningPeak, morningPeakWeekend, eveningPeakWeekend, offPeakWeekend, ZoneRule.G13S);
    }

    private enum ZoneRule
    {
        Single,
        G12,
        G12R,
        NightOnly,
        G12Season,
        G12Weekend,
        G13,
        G13Active,
        G13S
    }
}
