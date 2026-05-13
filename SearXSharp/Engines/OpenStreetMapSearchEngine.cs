using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for OpenStreetMap (nominatim.openstreetmap.org).
/// Uses Nominatim API (no API key required).
/// Based on SearXNG's openstreetmap.py.
/// </summary>
public class OpenStreetMapSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://nominatim.openstreetmap.org/search?{0}&polygon_geojson=1&format=jsonv2&addressdetails=1&extratags=1&dedupe=1";
    private const string _resultIdUrl = "https://openstreetmap.org/{0}/{1}";
    private const string _resultLatLonUrl = "https://www.openstreetmap.org/?mlat={0}&mlon={1}&zoom=12&layers=M";

    /// <inheritdoc />
    public override string Name => "openstreetmap";

    /// <inheritdoc />
    public override string DisplayName => "OpenStreetMap";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Map };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    public OpenStreetMapSearchEngine() : base() { }
    public OpenStreetMapSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var qs = string.Join("&", new Dictionary<string, string>
            {
                ["q"] = query.Query,
            }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var url = string.Format(_searchUrl, qs);

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("User-Agent", "SearXSharp/1.0");
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(json);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResult("timeout", suspended: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed for query: {Query}", Name, query.Query);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private SearchResultList ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var result in root.EnumerateArray())
            {
                try
                {
                    var lat = result.GetProperty("lat").GetString() ?? "0";
                    var lon = result.GetProperty("lon").GetString() ?? "0";
                    var displayName = result.GetProperty("display_name").GetString() ?? "";
                    var category = result.GetProperty("category").GetString() ?? "";
                    var type = result.GetProperty("type").GetString() ?? "";

                    // Build URL
                    string url;
                    if (result.TryGetProperty("osm_id", out var osmId))
                    {
                        var osmType = "";
                        if (result.TryGetProperty("osm_type", out var ot))
                            osmType = ot.GetString() ?? type;
                        else
                            osmType = type;

                        url = string.Format(_resultIdUrl, osmType, osmId.GetRawText());
                    }
                    else
                    {
                        url = string.Format(_resultLatLonUrl, lat, lon);
                    }

                    // Get address details
                    var address = "";
                    if (result.TryGetProperty("address", out var addrEl))
                    {
                        var addrParts = new List<string>();
                        foreach (var prop in addrEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                                addrParts.Add(prop.Value.GetString() ?? "");
                        }
                        address = string.Join(", ", addrParts);
                    }

                    // Thumbnail from icon if available
                    var thumbnail = "";
                    if (result.TryGetProperty("icon", out var iconEl))
                        thumbnail = iconEl.GetString() ?? "";

                    // Build content
                    var content = "";
                    var label = type;
                    if (!string.IsNullOrEmpty(category) && category != type)
                        label = $"{category} / {type}";
                    if (!string.IsNullOrEmpty(address))
                        content = $"{label} — {address}";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = displayName,
                        Content = content,
                        Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                        Latitude = double.TryParse(lat, out var dlat) ? dlat : 0,
                        Longitude = double.TryParse(lon, out var dlon) ? dlon : 0,
                        Engine = Name,
                        Category = SearchCategory.Map,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
