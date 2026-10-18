using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Tube.Api.Weather;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("sl", c =>
{
    c.BaseAddress = new Uri("https://transport.integration.sl.se/v1/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Tunnelkollen/0.1");
});
builder.Services.AddHttpClient("journeyplanner", c =>
{
    c.BaseAddress = new Uri("https://journeyplanner.integration.sl.se/v2/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Tunnelkollen/0.1");
});
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<SmhiWeatherService>();
var frontendOrigins = builder.Configuration["FrontendOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(frontendOrigins).AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "Tunnelkollen API" }));
app.MapGet("/api/geocode", async (string q, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { message = "Ange en plats eller adress." });
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Tunnelkollen/0.1 (Stockholm transit companion)");
    var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&q=" + Uri.EscapeDataString(q);
    var found = await http.GetFromJsonAsync<JsonElement[]>(url, ct);
    if (found is not { Length: > 0 }) return Results.NotFound(new { message = "Hittade ingen plats. Prova med en adress eller ett stationsnamn." });
    return Results.Ok(new { lat = double.Parse(found[0].GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture), lon = double.Parse(found[0].GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture), name = found[0].GetProperty("display_name").GetString() });
});
app.MapGet("/api/stations/nearby", async (double lat, double lon, int? limit, IHttpClientFactory clients, IMemoryCache cache, CancellationToken ct) =>
{
    if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
        return Results.BadRequest(new { message = "Koordinaterna är ogiltiga." });

    var sl = clients.CreateClient("sl");
    // Stop-finder coordinate lookups resolve to an address, not nearby transit stops.
    // Use SL stop-point metadata to identify metro station areas, then rank the
    // corresponding sites by distance. Cache this relatively static metadata.
    var metroStationsByAreaId = await cache.GetOrCreateAsync("sl-metro-stop-area-names", async entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
        var points = await sl.GetFromJsonAsync<JsonElement>("stop-points", ct);
        if (points.ValueKind != JsonValueKind.Array) return new Dictionary<int, string>();
        return points.EnumerateArray()
            .Where(point => point.TryGetProperty("stop_area", out var area)
                && area.TryGetProperty("type", out var type)
                && type.GetString() == "METROSTN"
                && area.TryGetProperty("name", out _))
            .Select(point =>
            {
                var area = point.GetProperty("stop_area");
                return new { Id = area.GetProperty("id").GetInt32(), Name = area.GetProperty("name").GetString() ?? "" };
            })
            .Where(area => !string.IsNullOrWhiteSpace(area.Name))
            .GroupBy(area => area.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);
    }) ?? new Dictionary<int, string>();
    if (!cache.TryGetValue<JsonElement>("sl-expanded-sites", out var sites))
    {
        sites = await sl.GetFromJsonAsync<JsonElement>("sites?expand=true", ct);
        if (sites.ValueKind == JsonValueKind.Array)
            cache.Set("sl-expanded-sites", sites.Clone(), TimeSpan.FromHours(12));
    }
    if (sites.ValueKind != JsonValueKind.Array) return Results.Problem("SL:s stationslista kunde inte läsas.", statusCode: StatusCodes.Status502BadGateway);
    var result = sites.EnumerateArray()
        .Where(site => site.TryGetProperty("lat", out _) && site.TryGetProperty("lon", out _)
            && site.TryGetProperty("stop_areas", out var areas)
            && areas.EnumerateArray().Any(area =>
            {
                var areaId = area.GetInt32();
                if (!metroStationsByAreaId.TryGetValue(areaId, out var stationName)) return false;
                var rawName = site.GetProperty("name").GetString() ?? "";
                var siteName = rawName.StartsWith("Stockholm ", StringComparison.OrdinalIgnoreCase) ? rawName["Stockholm ".Length..] : rawName;
                return NormalizeStationName(siteName) == NormalizeStationName(stationName);
            }))
        .Select(site =>
        {
            var rawName = site.GetProperty("name").GetString() ?? "";
            var name = rawName.StartsWith("Stockholm ", StringComparison.OrdinalIgnoreCase) ? rawName["Stockholm ".Length..] : rawName;
            var siteLat = site.GetProperty("lat").GetDouble();
            var siteLon = site.GetProperty("lon").GetDouble();
            return new { Id = site.GetProperty("id").GetInt32(), Name = name, Lat = siteLat, Lon = siteLon, Distance = DistanceKm(lat, lon, siteLat, siteLon) };
        })
        .GroupBy(site => NormalizeStationName(site.Name), StringComparer.Ordinal)
        .Select(group => group.OrderBy(site => site.Distance).First())
        .OrderBy(site => site.Distance)
        .Take(Math.Clamp(limit ?? 2, 1, 5))
        .Select(site => new { site.Id, site.Name, site.Lat, site.Lon, site.Distance });
    return Results.Ok(result);
});
app.MapGet("/api/trips", async (double lat, double lon, string destination, string? originStation, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(destination)) return Results.BadRequest(new { message = "Ange en destination." });
    if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
        return Results.BadRequest(new { message = "Koordinaterna är ogiltiga." });

    var planner = clients.CreateClient("journeyplanner");
    var destinationStopId = await FindBestStopIdAsync(planner, destination, ct);
    if (destinationStopId is null)
        return Results.NotFound(new { message = $"Hittade ingen SL-hållplats för {destination}." });

    var originStopId = string.IsNullOrWhiteSpace(originStation) ? null : await FindBestStopIdAsync(planner, originStation, ct);
    if (!string.IsNullOrWhiteSpace(originStation) && originStopId is null)
        return Results.NotFound(new { message = $"Hittade ingen SL-hållplats för stationen {originStation}." });

    var originType = originStopId is null ? "coord" : "any";
    var originName = originStopId ?? $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}:WGS84[dd.ddddd]";
    var query = $"type_origin={originType}&name_origin={Uri.EscapeDataString(originName)}&type_destination=any&name_destination={Uri.EscapeDataString(destinationStopId)}&calc_number_of_trips=3&calc_one_direction=true&incl_mot_2=true&incl_mot_5=false&language=sv";
    var result = await planner.GetFromJsonAsync<JsonElement>($"trips?{query}", ct);
    return Results.Ok(result);
});
app.MapGet("/api/stations/{siteId:int}/departures", async (int siteId, string? direction, IHttpClientFactory clients, CancellationToken ct) =>
{
    var http = clients.CreateClient("sl");
    var data = await http.GetFromJsonAsync<JsonElement>($"sites/{siteId}/departures", ct);
    if (!data.TryGetProperty("departures", out var departures)) return Results.Ok(Array.Empty<object>());
    var metro = departures.EnumerateArray().Where(d => d.TryGetProperty("line", out var line) && line.TryGetProperty("group_of_lines", out var group) && (group.GetString() ?? "").Contains("tunnelban", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(direction)) metro = metro.Where(d => (d.TryGetProperty("direction", out var dir) ? dir.GetString() : "")?.Contains(direction, StringComparison.OrdinalIgnoreCase) == true);
    // Keep the full upcoming metro board. A fixed limit can hide less frequent
    // lines (for example line 17 at T-Centralen) from the station's line list.
    return Results.Ok(metro);
});
app.MapGet("/api/weather", async Task<IResult> (double lat, double lon, SmhiWeatherService weather, CancellationToken ct) =>
{
    if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
        return Results.BadRequest(new { message = "Koordinaterna är ogiltiga." });

    try
    {
        return Results.Ok(await weather.GetForecastAsync(lat, lon, ct));
    }
    catch (HttpRequestException)
    {
        return Results.Problem("SMHI:s vädertjänst kunde inte nås just nu.", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.Run();

static async Task<string?> FindBestStopIdAsync(HttpClient planner, string name, CancellationToken ct)
{
    var query = $"stop-finder?type_sf=any&name_sf={Uri.EscapeDataString(name)}&any_obj_filter_sf=2";
    var response = await planner.GetFromJsonAsync<JsonElement>(query, ct);
    if (!response.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() == 0)
        return null;

    var matches = locations.EnumerateArray().ToArray();
    var metroMatches = matches.Where(location => location.TryGetProperty("productClasses", out var productClasses)
        && productClasses.ValueKind == JsonValueKind.Array
        && productClasses.EnumerateArray().Any(productClass => productClass.GetInt32() == 2)).ToArray();
    var candidates = metroMatches.Length > 0 ? metroMatches : matches;
    var best = candidates.FirstOrDefault(location => location.TryGetProperty("isBest", out var isBest) && isBest.ValueKind == JsonValueKind.True);
    if (best.ValueKind == JsonValueKind.Undefined) best = candidates[0];
    return best.TryGetProperty("id", out var id) ? id.GetString() : null;
}

static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
{
    var dLat = (lat2 - lat1) * Math.PI / 180; var dLon = (lon2 - lon1) * Math.PI / 180;
    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}
static string NormalizeStationName(string value)
{
    return new string(value
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
}
