using System.Net.Http.Json;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("sl", c =>
{
    c.BaseAddress = new Uri("https://transport.integration.sl.se/v1/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Tunnelkollen/0.1");
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
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
app.MapGet("/api/stations/nearby", async (double lat, double lon, int? limit, IHttpClientFactory clients, CancellationToken ct) =>
{
    // SL Journey planner returns nearby stops and productClasses; class 2 is metro.
    var journey = new HttpClient();
    var coord = $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}:WGS84[dd.ddddd]";
    var finder = await journey.GetFromJsonAsync<JsonElement>("https://journeyplanner.integration.sl.se/v2/stop-finder?type_sf=coord&name_sf=" + Uri.EscapeDataString(coord) + "&any_obj_filter_sf=2", ct);
    if (!finder.TryGetProperty("locations", out var locations)) return Results.Ok(Array.Empty<object>());
    var sl = clients.CreateClient("sl");
    var sites = await sl.GetFromJsonAsync<JsonElement>("sites?expand=true", ct);
    var candidates = locations.EnumerateArray()
        .Where(x => x.TryGetProperty("productClasses", out var pc) && pc.EnumerateArray().Any(c => c.GetInt32() == 2) && x.TryGetProperty("coord", out _))
        .Select(x => new { Name = x.TryGetProperty("disassembledName", out var dn) ? dn.GetString() ?? "" : x.GetProperty("name").GetString() ?? "", Lat = x.GetProperty("coord")[0].GetDouble(), Lon = x.GetProperty("coord")[1].GetDouble() })
        .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
    var siteRows = sites.EnumerateArray().Where(s => s.TryGetProperty("lat", out _) && s.TryGetProperty("lon", out _)).ToArray();
    var result = candidates.Select(c =>
    {
        var s = siteRows.Where(x => NameDistance(c.Name, x.GetProperty("name").GetString() ?? "") <= 1).OrderBy(x => NameDistance(c.Name, x.GetProperty("name").GetString() ?? "")).FirstOrDefault();
        return s.ValueKind == JsonValueKind.Undefined ? null : new { Id = s.GetProperty("id").GetInt32(), Name = c.Name, Lat = c.Lat, Lon = c.Lon, Distance = DistanceKm(lat, lon, c.Lat, c.Lon) };
    }).Where(x => x is not null).OrderBy(x => x!.Distance).Take(Math.Clamp(limit ?? 2, 1, 5));
    return Results.Ok(result);
});
app.MapGet("/api/trips", async (double lat, double lon, string destination, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(destination)) return Results.BadRequest(new { message = "Ange en destination." });
    var origin = $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}:WGS84[dd.ddddd]";
    var query = $"type_origin=coord&name_origin={Uri.EscapeDataString(origin)}&type_destination=any&name_destination={Uri.EscapeDataString(destination)}&calc_number_of_trips=3&incl_mot_2=true&incl_mot_5=false&language=sv";
    using var http = new HttpClient();
    var result = await http.GetFromJsonAsync<JsonElement>($"https://journeyplanner.integration.sl.se/v2/trips?{query}", ct);
    return Results.Ok(result);
});
app.MapGet("/api/stations/{siteId:int}/departures", async (int siteId, string? direction, IHttpClientFactory clients, CancellationToken ct) =>
{
    var http = clients.CreateClient("sl");
    var data = await http.GetFromJsonAsync<JsonElement>($"sites/{siteId}/departures", ct);
    if (!data.TryGetProperty("departures", out var departures)) return Results.Ok(Array.Empty<object>());
    var metro = departures.EnumerateArray().Where(d => d.TryGetProperty("line", out var line) && line.TryGetProperty("group_of_lines", out var group) && (group.GetString() ?? "").Contains("tunnelban", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(direction)) metro = metro.Where(d => (d.TryGetProperty("direction", out var dir) ? dir.GetString() : "")?.Contains(direction, StringComparison.OrdinalIgnoreCase) == true);
    return Results.Ok(metro.Take(12));
});
app.MapGet("/api/weather", (double lat, double lon) => Results.Ok(new { available = false, message = "Väder kopplas in i nästa steg.", lat, lon }));
app.Run();

static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
{
    var dLat = (lat2 - lat1) * Math.PI / 180; var dLon = (lon2 - lon1) * Math.PI / 180;
    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}
static int NameDistance(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 0 : b.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
