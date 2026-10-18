using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace Tube.Api.Weather;

public sealed class SmhiWeatherService(HttpClient http, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private const string Endpoint = "https://opendata-download-metfcst.smhi.se/api/category/snow1g/version/1/geotype/point/lon/{0}/lat/{1}/data.json?timeseries=24&parameters=air_temperature,wind_speed,probability_of_precipitation,precipitation_amount_mean,symbol_code";

    public async Task<WeatherForecastDto> GetForecastAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        var cacheKey = $"smhi:{latitude.ToString("F3", CultureInfo.InvariantCulture)}:{longitude.ToString("F3", CultureInfo.InvariantCulture)}";
        if (cache.TryGetValue(cacheKey, out WeatherForecastDto? cached) && cached is not null)
            return cached;

        var lat = latitude.ToString("F6", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F6", CultureInfo.InvariantCulture);
        var response = await http.GetFromJsonAsync<SmhiPointForecast>(string.Format(CultureInfo.InvariantCulture, Endpoint, lon, lat), cancellationToken);
        if (response?.TimeSeries is null || response.TimeSeries.Count == 0)
            throw new InvalidOperationException("SMHI svarade utan prognosdata för positionen.");
        var timeSeries = response.TimeSeries;

        var points = timeSeries.Select(ToPoint).ToArray();
        var now = DateTimeOffset.UtcNow;
        var current = points.FirstOrDefault(p => p.Time >= now.AddMinutes(-30)) ?? points[0];
        var swedishTimeZone = GetSwedishTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, swedishTimeZone);
        var eveningWindowStart = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 18, 0, 0, localNow.Offset);
        var eveningStart = eveningWindowStart > now ? eveningWindowStart : now;
        var eveningEnd = eveningWindowStart.AddHours(6);
        var eveningLow = points
            .Where(p => p.Time >= eveningStart && p.Time < eveningEnd && p.TemperatureC.HasValue)
            .MinBy(p => p.TemperatureC);
        var rainValues = points
            .Where(p => p.Time >= now && p.Time < now.AddHours(6))
            .Select(p => p.PrecipitationProbabilityPct)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToArray();
        double? rainProbability = rainValues.Length == 0 ? null : rainValues.Max();

        var forecast = new WeatherForecastDto(
            Source: "SMHI",
            ForecastUpdatedAt: response.CreatedTime,
            Current: current,
            EveningLow: eveningLow,
            RainProbabilityNext6HoursPct: rainProbability,
            NextHours: points.Where(p => p.Time >= now).Take(12).ToArray());

        cache.Set(cacheKey, forecast, CacheDuration);
        return forecast;
    }

    private static WeatherPoint ToPoint(SmhiTimeSeries item)
    {
        var symbolCode = ReadInt(item.Data, "symbol_code");
        return new WeatherPoint(
            Time: item.Time,
            TemperatureC: ReadNumber(item.Data, "air_temperature"),
            WindSpeedMs: ReadNumber(item.Data, "wind_speed"),
            PrecipitationProbabilityPct: ReadNumber(item.Data, "probability_of_precipitation"),
            PrecipitationMm: ReadNumber(item.Data, "precipitation_amount_mean"),
            SymbolCode: symbolCode,
            Condition: DescribeSymbol(symbolCode));
    }

    private static double? ReadNumber(Dictionary<string, double> data, string key)
        => data.TryGetValue(key, out var value) && value != 9999 && value >= 0 ? value : null;

    private static int? ReadInt(Dictionary<string, double> data, string key)
        => data.TryGetValue(key, out var value) && value is >= 1 and <= 27 ? (int)value : null;

    private static string DescribeSymbol(int? code) => code switch
    {
        1 => "Klart",
        2 => "Nästan klart",
        3 or 4 => "Växlande molnighet",
        5 => "Molnigt",
        6 => "Mulet",
        7 => "Dimma",
        8 => "Lätta regnskurar",
        9 => "Regnskurar",
        10 => "Kraftiga regnskurar",
        11 or 21 => "Åska",
        12 or 13 or 14 or 22 or 23 or 24 => "Snöblandat regn",
        15 or 16 or 17 => "Snöbyar",
        18 => "Lätt regn",
        19 => "Regn",
        20 => "Kraftigt regn",
        25 => "Lätt snöfall",
        26 => "Snöfall",
        27 => "Kraftigt snöfall",
        _ => "Prognos"
    };

    private static TimeZoneInfo GetSwedishTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }
}

public sealed record WeatherForecastDto(
    string Source,
    DateTimeOffset ForecastUpdatedAt,
    WeatherPoint Current,
    WeatherPoint? EveningLow,
    double? RainProbabilityNext6HoursPct,
    IReadOnlyList<WeatherPoint> NextHours);

public sealed record WeatherPoint(
    DateTimeOffset Time,
    double? TemperatureC,
    double? WindSpeedMs,
    double? PrecipitationProbabilityPct,
    double? PrecipitationMm,
    int? SymbolCode,
    string Condition);

internal sealed class SmhiPointForecast
{
    [JsonPropertyName("createdTime")]
    public DateTimeOffset CreatedTime { get; init; }

    [JsonPropertyName("referenceTime")]
    public DateTimeOffset ReferenceTime { get; init; }

    [JsonPropertyName("timeSeries")]
    public List<SmhiTimeSeries>? TimeSeries { get; init; }
}

internal sealed class SmhiTimeSeries
{
    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    [JsonPropertyName("data")]
    public Dictionary<string, double> Data { get; init; } = new();
}
