using System.Globalization;
using Microsoft.AspNetCore.Mvc;
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
builder.Services.AddHostedService<PriceRefreshWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/health"));

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
    PriceCache cache,
    CancellationToken cancellationToken) =>
{
    await cache.EnsureFreshAsync(cancellationToken);
    var now = DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset());

    var response = new Dictionary<string, decimal?>();

    foreach (var offset in Enumerable.Range(0, 24))
    {
        var targetHour = now.AddHours(offset);
        response[$"h{offset}"] = cache.TryGetPrice(targetHour, unit, out var price)
            ? price
            : null;
    }

    return Results.Ok(response);
});

app.MapGet("/loxone/h{offset:int}", async (
    int offset,
    [FromQuery] string? unit,
    PriceCache cache,
    CancellationToken cancellationToken) =>
{
    if (offset is < 0 or > 23)
    {
        return Results.BadRequest("offset must be between 0 and 23");
    }

    await cache.EnsureFreshAsync(cancellationToken);
    var targetHour = DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset()).AddHours(offset);
    return cache.TryGetPrice(targetHour, unit, out var price)
        ? LoxoneNumber(price)
        : Results.NotFound("price unavailable");
});

app.MapGet("/loxone/relative/{offset:int}", async (
    int offset,
    [FromQuery] string? unit,
    PriceCache cache,
    CancellationToken cancellationToken) =>
{
    if (offset is < 0 or > 23)
    {
        return Results.BadRequest("offset must be between 0 and 23");
    }

    await cache.EnsureFreshAsync(cancellationToken);
    var targetHour = DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset()).AddHours(offset);
    return cache.TryGetPrice(targetHour, unit, out var price)
        ? LoxoneNumber(price)
        : Results.NotFound("price unavailable");
});

app.MapGet("/loxone/today/{hour:int}", async (
    int hour,
    [FromQuery] string? unit,
    PriceCache cache,
    CancellationToken cancellationToken) =>
{
    if (hour is < 0 or > 23)
    {
        return Results.BadRequest("hour must be between 0 and 23");
    }

    await cache.EnsureFreshAsync(cancellationToken);
    var now = DateTimeOffset.Now.ToOffset(PriceCache.WarsawOffset());
    var targetHour = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
    return cache.TryGetPrice(targetHour, unit, out var price)
        ? LoxoneNumber(price)
        : Results.NotFound("price unavailable");
});

app.MapPost("/admin/refresh", async (PriceCache cache, CancellationToken cancellationToken) =>
{
    var result = await cache.RefreshAsync(force: true, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static IResult LoxoneNumber(decimal price)
{
    return Results.Text(price.ToString("0.#####", CultureInfo.InvariantCulture), "text/plain");
}
