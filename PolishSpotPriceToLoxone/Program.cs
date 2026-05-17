using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PolishSpotPriceToLoxone.Models;
using PolishSpotPriceToLoxone.Options;
using PolishSpotPriceToLoxone.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<PriceOptions>(builder.Configuration.GetSection(PriceOptions.SectionName));
builder.Services.AddHttpClient<TgeRdnClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PolishSpotPriceToLoxone/1.0");
});
builder.Services.AddSingleton<PriceCache>();
builder.Services.AddSingleton<AzureSqlPriceStore>();
builder.Services.AddSingleton<HistoricalPriceCache>();
builder.Services.AddHostedService<PriceRefreshWorker>();
builder.Services.AddHostedService<HistoricalPriceRefreshWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Text(GetDashboardHtml(), "text/html; charset=utf-8"));

app.MapGet("/dashboard", () => Results.Text(GetDashboardHtml(), "text/html; charset=utf-8"));

app.MapGet("/docs", () => Results.Redirect("/loxone/docs"));

app.MapGet("/loxone/docs", () => Results.Text(GetLoxoneDocsHtml(), "text/html; charset=utf-8"));

app.MapGet("/loxone/tariffs", () => Results.Ok(PriceAdjustment.SupportedTariffs()));

app.MapGet("/health", (PriceCache cache) =>
{
    var snapshot = cache.GetSnapshot();
    return Results.Ok(new
    {
        status = snapshot.HasAnyPrices ? "ok" : "warming-up",
        snapshot.State,
        snapshot.LastSuccessfulRefreshUtc,
        snapshot.LastAttemptUtc,
        snapshot.NextAttemptUtc,
        snapshot.PriceCount,
        snapshot.Dates,
        snapshot.LastError
    });
});

app.MapGet("/api/prices", async (PriceCache cache, CancellationToken cancellationToken) =>
{
    await cache.EnsureFreshAsync(cancellationToken);
    return Results.Ok(cache.GetSnapshot());
});

app.MapGet("/loxone/prices", async (
    [FromQuery] string? unit,
    [FromQuery(Name = "date")] string? dateText,
    [FromQuery] bool? pstryk,
    [FromQuery] string? seller,
    [FromQuery] string? distributor,
    [FromQuery] string? distribution,
    [FromQuery] string? tariff,
    [FromQuery] bool? vat,
    [FromQuery] bool? gross,
    [FromQuery] bool? brutto,
    PriceCache cache,
    HistoricalPriceCache historicalCache,
    CancellationToken cancellationToken) =>
{
    if (!PriceAdjustment.TryCreate(pstryk, seller, distributor, distribution, tariff, vat, gross, brutto, out var adjustment, out var error))
    {
        return Results.BadRequest(error);
    }

    if (!TryParseDate(dateText, out var requestedDate, out var dateError))
    {
        return Results.BadRequest(dateError);
    }

    var startHour = requestedDate is null
        ? DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset())
        : new DateTimeOffset(requestedDate.Value.ToDateTime(TimeOnly.MinValue), PriceCache.GetWarsawTimeZone().GetUtcOffset(requestedDate.Value.ToDateTime(TimeOnly.MinValue)));
    IReadOnlyList<HourlyPrice>? historicalPrices = null;
    historicalPrices = await GetRequestedDatePricesAsync(requestedDate, cache, historicalCache, cancellationToken);

    var response = new Dictionary<string, decimal?>();

    foreach (var offset in Enumerable.Range(0, 24))
    {
        var targetHour = startHour.AddHours(offset);
        response[$"h{offset}"] = TryGetPrice(cache, historicalPrices, targetHour, unit, out var price)
            ? adjustment.Apply(targetHour, price, unit)
            : null;
    }

    return Results.Ok(response);
});

app.MapGet("/loxone/h{offset:int}", async (
    int offset,
    [FromQuery] string? unit,
    [FromQuery(Name = "date")] string? dateText,
    [FromQuery] bool? pstryk,
    [FromQuery] string? seller,
    [FromQuery] string? distributor,
    [FromQuery] string? distribution,
    [FromQuery] string? tariff,
    [FromQuery] bool? vat,
    [FromQuery] bool? gross,
    [FromQuery] bool? brutto,
    PriceCache cache,
    HistoricalPriceCache historicalCache,
    CancellationToken cancellationToken) =>
{
    if (offset is < 0 or > 23)
    {
        return Results.BadRequest("offset must be between 0 and 23");
    }

    if (!PriceAdjustment.TryCreate(pstryk, seller, distributor, distribution, tariff, vat, gross, brutto, out var adjustment, out var error))
    {
        return Results.BadRequest(error);
    }

    if (!TryParseDate(dateText, out var requestedDate, out var dateError))
    {
        return Results.BadRequest(dateError);
    }

    var targetHour = requestedDate is null
        ? DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset()).AddHours(offset)
        : new DateTimeOffset(requestedDate.Value.ToDateTime(TimeOnly.MinValue), PriceCache.GetWarsawTimeZone().GetUtcOffset(requestedDate.Value.ToDateTime(TimeOnly.MinValue))).AddHours(offset);
    IReadOnlyList<HourlyPrice>? historicalPrices = null;
    historicalPrices = await GetRequestedDatePricesAsync(requestedDate, cache, historicalCache, cancellationToken);

    return TryGetPrice(cache, historicalPrices, targetHour, unit, out var price)
        ? LoxoneNumber(adjustment.Apply(targetHour, price, unit))
        : Results.NotFound("price unavailable");
});

app.MapGet("/loxone/relative/{offset:int}", async (
    int offset,
    [FromQuery] string? unit,
    [FromQuery(Name = "date")] string? dateText,
    [FromQuery] bool? pstryk,
    [FromQuery] string? seller,
    [FromQuery] string? distributor,
    [FromQuery] string? distribution,
    [FromQuery] string? tariff,
    [FromQuery] bool? vat,
    [FromQuery] bool? gross,
    [FromQuery] bool? brutto,
    PriceCache cache,
    HistoricalPriceCache historicalCache,
    CancellationToken cancellationToken) =>
{
    if (offset is < 0 or > 23)
    {
        return Results.BadRequest("offset must be between 0 and 23");
    }

    if (!PriceAdjustment.TryCreate(pstryk, seller, distributor, distribution, tariff, vat, gross, brutto, out var adjustment, out var error))
    {
        return Results.BadRequest(error);
    }

    if (!TryParseDate(dateText, out var requestedDate, out var dateError))
    {
        return Results.BadRequest(dateError);
    }

    var targetHour = requestedDate is null
        ? DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset()).AddHours(offset)
        : new DateTimeOffset(requestedDate.Value.ToDateTime(TimeOnly.MinValue), PriceCache.GetWarsawTimeZone().GetUtcOffset(requestedDate.Value.ToDateTime(TimeOnly.MinValue))).AddHours(offset);
    IReadOnlyList<HourlyPrice>? historicalPrices = null;
    historicalPrices = await GetRequestedDatePricesAsync(requestedDate, cache, historicalCache, cancellationToken);

    return TryGetPrice(cache, historicalPrices, targetHour, unit, out var price)
        ? LoxoneNumber(adjustment.Apply(targetHour, price, unit))
        : Results.NotFound("price unavailable");
});

app.MapGet("/loxone/today/{hour:int}", async (
    int hour,
    [FromQuery] string? unit,
    [FromQuery(Name = "date")] string? dateText,
    [FromQuery] bool? pstryk,
    [FromQuery] string? seller,
    [FromQuery] string? distributor,
    [FromQuery] string? distribution,
    [FromQuery] string? tariff,
    [FromQuery] bool? vat,
    [FromQuery] bool? gross,
    [FromQuery] bool? brutto,
    PriceCache cache,
    HistoricalPriceCache historicalCache,
    CancellationToken cancellationToken) =>
{
    if (hour is < 0 or > 23)
    {
        return Results.BadRequest("hour must be between 0 and 23");
    }

    if (!PriceAdjustment.TryCreate(pstryk, seller, distributor, distribution, tariff, vat, gross, brutto, out var adjustment, out var error))
    {
        return Results.BadRequest(error);
    }

    if (!TryParseDate(dateText, out var requestedDate, out var dateError))
    {
        return Results.BadRequest(dateError);
    }

    var baseDate = requestedDate?.ToDateTime(TimeOnly.MinValue) ?? DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset()).Date;
    var targetHour = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, hour, 0, 0, PriceCache.GetWarsawTimeZone().GetUtcOffset(baseDate));
    IReadOnlyList<HourlyPrice>? historicalPrices = null;
    historicalPrices = await GetRequestedDatePricesAsync(requestedDate, cache, historicalCache, cancellationToken);

    return TryGetPrice(cache, historicalPrices, targetHour, unit, out var price)
        ? LoxoneNumber(adjustment.Apply(targetHour, price, unit))
        : Results.NotFound("price unavailable");
});

app.MapPost("/admin/refresh", async (PriceCache cache, CancellationToken cancellationToken) =>
{
    var result = await cache.RefreshAsync(force: true, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/admin/historical/refresh", async (
    [FromQuery(Name = "date_from")] string? dateFromText,
    [FromQuery(Name = "date_to")] string? dateToText,
    HistoricalPriceCache historicalCache,
    CancellationToken cancellationToken) =>
{
    if (!TryParseRequiredDate(dateFromText, "date_from", out var dateFrom, out var dateFromError))
    {
        return Results.BadRequest(dateFromError);
    }

    if (!TryParseRequiredDate(dateToText, "date_to", out var dateTo, out var dateToError))
    {
        return Results.BadRequest(dateToError);
    }

    var result = await historicalCache.RefreshRangeAsync(dateFrom, dateTo, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static IResult LoxoneNumber(decimal price)
{
    return Results.Text(price.ToString("0.#####", CultureInfo.InvariantCulture), "text/plain");
}

static bool TryParseDate(string? value, out DateOnly? date, out string? error)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        date = null;
        error = null;
        return true;
    }

    if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
    {
        date = parsed;
        error = null;
        return true;
    }

    date = null;
    error = "date must use yyyy-MM-dd format";
    return false;
}

static bool TryParseRequiredDate(string? value, string parameterName, out DateOnly date, out string? error)
{
    if (!string.IsNullOrWhiteSpace(value) &&
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
    {
        error = null;
        return true;
    }

    date = default;
    error = $"{parameterName} must use yyyy-MM-dd format";
    return false;
}

static async Task<IReadOnlyList<HourlyPrice>?> GetRequestedDatePricesAsync(
    DateOnly? requestedDate,
    PriceCache currentCache,
    HistoricalPriceCache historicalCache,
    CancellationToken cancellationToken)
{
    if (requestedDate is null)
    {
        await currentCache.EnsureFreshAsync(cancellationToken);
        return null;
    }

    var historicalPrices = await historicalCache.GetPricesAsync(requestedDate.Value, cancellationToken);
    if (historicalPrices.Count > 0)
    {
        return historicalPrices;
    }

    var today = DateOnly.FromDateTime(DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset()).Date);
    if (requestedDate.Value >= today && requestedDate.Value <= today.AddDays(1))
    {
        await currentCache.EnsureFreshAsync(cancellationToken);
        return null;
    }

    return historicalPrices;
}

static bool TryGetPrice(PriceCache currentCache, IReadOnlyList<HourlyPrice>? historicalPrices, DateTimeOffset targetHour, string? unit, out decimal price)
{
    if (historicalPrices is null)
    {
        return currentCache.TryGetPrice(targetHour, unit, out price);
    }

    var hourStart = new DateTimeOffset(targetHour.Year, targetHour.Month, targetHour.Day, targetHour.Hour, 0, 0, targetHour.Offset);
    var item = historicalPrices.FirstOrDefault(price => price.HourLocal == hourStart);
    if (item is null)
    {
        price = 0;
        return false;
    }

    price = ConvertUnit(item.PricePlnPerMwh, unit);
    return true;
}

static decimal ConvertUnit(decimal plnPerMwh, string? unit)
{
    return string.Equals(unit, "mwh", StringComparison.OrdinalIgnoreCase)
        ? plnPerMwh
        : Math.Round(plnPerMwh / 1000m, 5, MidpointRounding.AwayFromZero);
}

static string GetDashboardHtml()
{
    return """
<!doctype html>
<html lang="pl">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Ceny energii RDN</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f7f9;
      --panel: #ffffff;
      --text: #16191d;
      --muted: #5f6875;
      --line: #d9dee7;
      --accent: #176b87;
      --low: #e8f6ef;
      --mid: #fff7df;
      --high: #fdecec;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }

    * { box-sizing: border-box; }
    body { margin: 0; background: var(--bg); color: var(--text); }
    header { background: #ffffff; border-bottom: 1px solid var(--line); }
    .wrap { width: min(1180px, calc(100% - 32px)); margin: 0 auto; }
    .topbar { display: flex; align-items: center; justify-content: space-between; gap: 16px; min-height: 70px; }
    h1 { font-size: 22px; line-height: 1.2; margin: 0; font-weight: 750; letter-spacing: 0; }
    nav { display: flex; gap: 8px; flex-wrap: wrap; }
    nav a { color: var(--accent); text-decoration: none; font-weight: 650; font-size: 14px; }
    main { padding: 22px 0 40px; }
    .toolbar { display: grid; grid-template-columns: repeat(6, minmax(120px, 1fr)); gap: 10px; align-items: end; margin-bottom: 18px; }
    label { display: grid; gap: 5px; font-size: 12px; color: var(--muted); font-weight: 700; text-transform: uppercase; }
    select, input, button {
      height: 38px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #ffffff;
      color: var(--text);
      padding: 0 10px;
      font: inherit;
      min-width: 0;
    }
    button { background: var(--accent); color: #ffffff; border-color: var(--accent); font-weight: 750; cursor: pointer; }
    button.secondary { background: #ffffff; color: var(--accent); }
    .summary { display: grid; grid-template-columns: repeat(4, minmax(120px, 1fr)); gap: 10px; margin: 0 0 18px; }
    .metric { background: var(--panel); border: 1px solid var(--line); border-radius: 6px; padding: 12px; }
    .metric span { display: block; color: var(--muted); font-size: 12px; font-weight: 700; text-transform: uppercase; }
    .metric strong { display: block; margin-top: 5px; font-size: 22px; letter-spacing: 0; }
    .table-shell { background: var(--panel); border: 1px solid var(--line); border-radius: 6px; overflow: auto; }
    table { border-collapse: collapse; width: 100%; min-width: 720px; }
    th, td { padding: 10px 12px; border-bottom: 1px solid var(--line); text-align: right; white-space: nowrap; }
    th:first-child, td:first-child { text-align: left; }
    th { background: #fbfcfd; color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0; }
    tr:last-child td { border-bottom: 0; }
    tr.low td.price { background: var(--low); }
    tr.mid td.price { background: var(--mid); }
    tr.high td.price { background: var(--high); }
    .mono { font-variant-numeric: tabular-nums; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
    .status { min-height: 24px; color: var(--muted); margin: 10px 0 0; font-size: 14px; }
    .error { color: #b42318; }
    .url { display: block; margin-top: 12px; color: var(--muted); overflow-wrap: anywhere; font-size: 13px; }

    @media (max-width: 860px) {
      .topbar { align-items: flex-start; flex-direction: column; padding: 16px 0; }
      .toolbar { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .summary { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    }

    @media (max-width: 520px) {
      .wrap { width: min(100% - 20px, 1180px); }
      .toolbar, .summary { grid-template-columns: 1fr; }
      h1 { font-size: 20px; }
    }
  </style>
</head>
<body>
  <header>
    <div class="wrap topbar">
      <h1>Ceny energii RDN</h1>
      <nav>
        <a href="/loxone/docs">Dokumentacja</a>
        <a href="/loxone/tariffs">Taryfy JSON</a>
        <a href="/health">Health</a>
      </nav>
    </div>
  </header>

  <main class="wrap">
    <form id="filters" class="toolbar">
      <label>Data
        <input id="date" name="date" type="date">
      </label>
      <label>Dystrybutor
        <select id="distributor" name="distributor"></select>
      </label>
      <label>Taryfa
        <select id="tariff" name="tariff"></select>
      </label>
      <label>Sprzedawca
        <select id="seller" name="seller">
          <option value="">Bez marży</option>
          <option value="pstryk">Pstryk</option>
        </select>
      </label>
      <label>Jednostka
        <select id="unit" name="unit">
          <option value="kwh">zł/kWh</option>
          <option value="mwh">zł/MWh</option>
        </select>
      </label>
      <button type="submit">Odśwież</button>
    </form>

    <section class="summary" aria-label="Podsumowanie">
      <div class="metric"><span>Minimum</span><strong id="min">-</strong></div>
      <div class="metric"><span>Średnia</span><strong id="avg">-</strong></div>
      <div class="metric"><span>Maksimum</span><strong id="max">-</strong></div>
      <div class="metric"><span>Dostępne godziny</span><strong id="count">-</strong></div>
    </section>

    <div class="table-shell">
      <table>
        <thead>
          <tr>
            <th>Godzina</th>
            <th>Klucz</th>
            <th>Cena</th>
            <th>Poziom</th>
          </tr>
        </thead>
        <tbody id="rows"></tbody>
      </table>
    </div>
    <p id="status" class="status"></p>
    <code id="apiUrl" class="url"></code>
  </main>

  <script>
    const $ = (id) => document.getElementById(id);
    const distributor = $("distributor");
    const tariff = $("tariff");
    const rows = $("rows");
    let tariffs = {};

    function today() {
      const now = new Date();
      const offset = now.getTimezoneOffset();
      return new Date(now.getTime() - offset * 60000).toISOString().slice(0, 10);
    }

    function fmt(value) {
      return value === null || value === undefined ? "-" : Number(value).toFixed(2);
    }

    function setMetric(id, value, suffix) {
      $(id).textContent = value === null ? "-" : `${fmt(value)} ${suffix}`;
    }

    function fillDistributors() {
      distributor.innerHTML = '<option value="">TGE bez dystrybucji</option>';
      Object.keys(tariffs).forEach((name) => {
        const option = document.createElement("option");
        option.value = name;
        option.textContent = name;
        distributor.appendChild(option);
      });
      distributor.value = "tauron";
      fillTariffs();
      tariff.value = "g12w";
    }

    function fillTariffs() {
      const selected = distributor.value;
      tariff.innerHTML = '<option value="">Bez taryfy</option>';
      (tariffs[selected] || []).forEach((name) => {
        const option = document.createElement("option");
        option.value = name;
        option.textContent = name;
        tariff.appendChild(option);
      });
      tariff.disabled = !selected;
    }

    function buildUrl() {
      const params = new URLSearchParams();
      const date = $("date").value;
      if (date) params.set("date", date);
      if (distributor.value) params.set("distributor", distributor.value);
      if (tariff.value) params.set("tariff", tariff.value);
      if ($("seller").value) params.set("seller", $("seller").value);
      if ($("unit").value) params.set("unit", $("unit").value);
      return `/loxone/prices?${params.toString()}`;
    }

    function level(value, min, max) {
      if (value === null || value === undefined) return "";
      const span = max - min;
      if (span <= 0) return "mid";
      const position = (value - min) / span;
      if (position <= 0.33) return "low";
      if (position >= 0.67) return "high";
      return "mid";
    }

    async function loadPrices(event) {
      if (event) event.preventDefault();
      $("status").textContent = "Ładowanie...";
      $("status").className = "status";
      const url = buildUrl();
      $("apiUrl").textContent = url;
      try {
        const response = await fetch(url);
        if (!response.ok) throw new Error(await response.text());
        const data = await response.json();
        const items = Object.entries(data).map(([key, value], index) => ({ key, value, index }));
        const values = items.map((item) => item.value).filter((value) => value !== null && value !== undefined);
        const min = values.length ? Math.min(...values) : null;
        const max = values.length ? Math.max(...values) : null;
        const avg = values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : null;
        const unit = $("unit").value === "mwh" ? "zł/MWh" : "zł/kWh";

        setMetric("min", min, unit);
        setMetric("avg", avg, unit);
        setMetric("max", max, unit);
        $("count").textContent = `${values.length}/24`;

        rows.innerHTML = "";
        items.forEach((item) => {
          const tr = document.createElement("tr");
          const cls = level(item.value, min ?? 0, max ?? 0);
          if (cls) tr.className = cls;
          const hour = item.index.toString().padStart(2, "0") + ":00";
          const label = cls === "low" ? "taniej" : cls === "high" ? "drożej" : cls === "średnio" ? "średnio" : "średnio";
          tr.innerHTML = `
            <td class="mono">${hour}</td>
            <td class="mono">${item.key}</td>
            <td class="price mono">${fmt(item.value)} ${unit}</td>
            <td>${item.value === null || item.value === undefined ? "brak" : label}</td>
          `;
          rows.appendChild(tr);
        });
        $("status").textContent = "Zaktualizowano.";
      } catch (error) {
        rows.innerHTML = "";
        ["min", "avg", "max"].forEach((id) => $(id).textContent = "-");
        $("count").textContent = "-";
        $("status").textContent = error.message || "Nie udało się pobrać danych.";
        $("status").className = "status error";
      }
    }

    async function start() {
      $("date").value = today();
      const response = await fetch("/loxone/tariffs");
      tariffs = await response.json();
      fillDistributors();
      distributor.addEventListener("change", fillTariffs);
      $("filters").addEventListener("submit", loadPrices);
      await loadPrices();
    }

    start();
  </script>
</body>
</html>
""";
}

static string GetLoxoneDocsHtml()
{
    return """
<!doctype html>
<html lang="pl">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Polish Spot Price to Loxone - dokumentacja</title>
  <style>
    :root { color-scheme: light dark; font-family: system-ui, -apple-system, Segoe UI, sans-serif; }
    body { margin: 0; line-height: 1.5; }
    main { max-width: 980px; margin: 0 auto; padding: 32px 20px 56px; }
    h1 { font-size: 32px; margin: 0 0 8px; }
    h2 { margin-top: 32px; border-top: 1px solid #ccc; padding-top: 24px; }
    code, pre { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
    pre { overflow-x: auto; padding: 14px; border: 1px solid #ccc; border-radius: 6px; }
    table { border-collapse: collapse; width: 100%; margin: 12px 0; }
    th, td { border: 1px solid #ccc; padding: 8px; text-align: left; vertical-align: top; }
    .muted { opacity: .78; }
  </style>
</head>
<body>
<main>
  <h1>Polish Spot Price to Loxone</h1>
  <p class="muted">Endpointy zwracają ceny energii w formacie wygodnym dla Loxone: <code>h0..h23</code> albo pojedynczą wartość tekstową.</p>

  <h2>Najważniejszy endpoint</h2>
  <pre>GET /loxone/prices</pre>
  <p>Zwraca JSON z 24 polami:</p>
  <pre>{
  "h0": 0.71,
  "h1": 0.70,
  "h2": 0.68
}</pre>
  <p>Bez parametru <code>date</code> zakres zaczyna się od aktualnej godziny. <code>h0</code> to bieżąca godzina, <code>h1</code> za godzinę, aż do <code>h23</code>.</p>
  <p>Z parametrem <code>date=YYYY-MM-DD</code> zakres zaczyna się od północy wybranego dnia. <code>h0</code> to 00:00, <code>h12</code> to 12:00, <code>h23</code> to 23:00.</p>

  <h2>Przykłady</h2>
  <table>
    <tr><th>Cel</th><th>URL</th></tr>
    <tr><td>Czyste ceny TGE, następne 24h</td><td><code>/loxone/prices</code></td></tr>
    <tr><td>Czyste ceny TGE od północy danego dnia</td><td><code>/loxone/prices?date=2026-05-17</code></td></tr>
    <tr><td>Domyslnie Tauron G12, bez marzy Pstryk</td><td><code>/loxone/prices?tariff=g12</code></td></tr>
    <tr><td>Tauron G12 + Pstryk</td><td><code>/loxone/prices?distributor=tauron&amp;tariff=g12&amp;seller=pstryk</code></td></tr>
    <tr><td>Energa G11F + Pstryk, konkretny dzien od 00:00</td><td><code>/loxone/prices?date=2026-05-17&amp;distributor=energa&amp;tariff=g11f&amp;seller=pstryk</code></td></tr>
    <tr><td>PGE G12W + Pstryk</td><td><code>/loxone/prices?distributor=pge&amp;tariff=g12w&amp;seller=pstryk</code></td></tr>
    <tr><td>Pojedyncza wartosc h0</td><td><code>/loxone/h0?distributor=stoen&amp;tariff=g12&amp;seller=pstryk</code></td></tr>
    <tr><td>Pojedyncza godzina z wybranego dnia</td><td><code>/loxone/today/12?date=2026-05-17&amp;distributor=enea&amp;tariff=g12w&amp;seller=pstryk</code></td></tr>
  </table>

  <h2>Parametry</h2>
  <table>
    <tr><th>Parametr</th><th>Opis</th></tr>
    <tr><td><code>date</code></td><td>Opcjonalna data w formacie <code>YYYY-MM-DD</code>. Jeśli jest podana, liczymy od 00:00 tego dnia. Jeśli nie ma daty, liczymy od aktualnej godziny.</td></tr>
    <tr><td><code>tariff</code></td><td>Opcjonalna taryfa dystrybucyjna. Pelna lista jest w <code>/loxone/tariffs</code>. Obslugujemy taryfy widoczne na stronie Pstryk dla: Energa, Tauron, PGE, Enea i Stoen.</td></tr>
    <tr><td><code>distributor</code> lub <code>distribution</code></td><td>Opcjonalny operator dystrybucyjny: <code>tauron</code>, <code>energa</code>, <code>pge</code>, <code>enea</code>, <code>stoen</code>. Jesli podasz sama taryfe, dystrybutor domyslnie ustawia sie na Tauron.</td></tr>
    <tr><td><code>seller</code></td><td>Opcjonalny sprzedawca. Gdy <code>seller=pstryk</code>, doliczana jest marża Pstryk: <code>0.08 zł/kWh</code>. Gdy parametru nie ma, marża nie jest doliczana.</td></tr>
    <tr><td><code>unit</code></td><td>Domyślnie <code>kwh</code>. Można podać <code>mwh</code>, wtedy wynik jest w PLN/MWh.</td></tr>
  </table>

  <h2>Jak liczymy</h2>
  <p>Bez taryfy zwracamy prostą cenę z TGE RDN:</p>
  <pre>cena = cena_RDN / 1000</pre>
  <p>Wynik jest w <code>zł/kWh</code>.</p>

  <p>Jeśli podasz taryfę, doliczamy zmienne koszty dystrybucji i VAT. Tak jak w tabeli Pstryk najpierw zaokrąglamy część energii z RDN do 2 miejsc, potem liczymy VAT i wynik brutto:</p>
  <pre>koszt_netto =
  round(cena_RDN / 1000, 2)
  + zmienna_dystrybucja_OSD
  + opłata_jakościowa
  + opłata_OZE
  + opłata_kogeneracyjna
  + akcyza
  + (seller == "pstryk" ? 0.08 : 0)

koszt_brutto = koszt_netto * 1.23</pre>
  <p>Gdy podana jest taryfa, endpoint zwraca cene brutto. Stalych oplat miesiecznych nie uwzgledniamy: abonamentowej, mocowej i stalej sieciowej.</p>

  <h2>Dystrybutorzy i taryfy</h2>
  <p>JSON z pelna lista obslugiwanych wariantow:</p>
  <pre>GET /loxone/tariffs</pre>
  <table>
    <tr><th>Dystrybutor</th><th>Taryfy</th></tr>
    <tr><td><code>tauron</code></td><td><code>g11</code>, <code>g12</code>, <code>g12w</code>, <code>g13</code>, <code>g13s</code></td></tr>
    <tr><td><code>energa</code></td><td><code>g11</code>, <code>g11f</code>, <code>g12</code>, <code>g12r</code>, <code>g12w</code></td></tr>
    <tr><td><code>pge</code></td><td><code>g11</code>, <code>g12</code>, <code>g12e</code>, <code>g12n</code>, <code>g12w</code></td></tr>
    <tr><td><code>enea</code></td><td><code>g11</code>, <code>g12</code>, <code>g12sezon</code>, <code>g12w</code>, <code>g13active</code></td></tr>
    <tr><td><code>stoen</code></td><td><code>g11</code>, <code>g12</code>, <code>g12w</code></td></tr>
  </table>

  <h2>Strefy czasowe taryf</h2>
  <table>
    <tr><th>Taryfa</th><th>Zasada</th></tr>
    <tr><td><code>g11</code></td><td>Jedna strefa przez całą dobę.</td></tr>
    <tr><td><code>g12</code></td><td>Niższa strefa: 22:00-06:00 oraz 13:00-15:00. Pozostałe godziny: szczyt.</td></tr>
    <tr><td><code>g12r</code></td><td>Nizsza strefa: 22:00-07:00 oraz 13:00-15:00. Pozostale godziny: szczyt.</td></tr>
    <tr><td><code>g12e</code>, <code>g12n</code>, <code>g12sezon</code>, <code>g13active</code>, <code>g13s</code></td><td>Specjalne modele strefowe operatorow wedlug stawek OSD i tabeli Pstryk.</td></tr>
    <tr><td><code>g12w</code></td><td>Jak G12, dodatkowo weekendy i polskie święta są w niższej strefie.</td></tr>
    <tr><td><code>g13</code></td><td>Trzy strefy Tauron: przedpołudniowa, popołudniowa i pozostała; weekendy i święta są w najniższej strefie.</td></tr>
  </table>

  <h2>Inne endpointy</h2>
  <table>
    <tr><th>Endpoint</th><th>Opis</th></tr>
    <tr><td><code>GET /loxone/h{offset}</code></td><td>Pojedyncza wartość tekstowa. <code>/loxone/h0</code> to aktualna godzina albo 00:00 przy podanym <code>date</code>.</td></tr>
    <tr><td><code>GET /loxone/relative/{offset}</code></td><td>Alias dla pojedynczej wartości relatywnej, 0-23.</td></tr>
    <tr><td><code>GET /loxone/today/{hour}</code></td><td>Pojedyncza godzina dnia, 0-23. Z <code>date</code> oznacza godzinę wybranego dnia.</td></tr>
    <tr><td><code>GET /loxone/tariffs</code></td><td>Lista obslugiwanych dystrybutorow i taryf.</td></tr>
    <tr><td><code>GET /health</code></td><td>Status aplikacji i cache.</td></tr>
  </table>
</main>
</body>
</html>
""";
}
