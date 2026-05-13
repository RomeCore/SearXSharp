using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Photon (photon.komoot.io).
/// Photon is an open source geocoder for OpenStreetMap data.
/// Based on SearXNG's photon.py.
/// </summary>
public class PhotonSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://photon.komoot.io";
    private const string _resultBaseUrl = "https://openstreetmap.org/{osm_type}/{osm_id}";
    private const int _limit = 10;

    private static readonly HashSet<string> _supportedLanguages = new() { "de", "en", "fr", "it" };

    /// <inheritdoc />
    public override string Name => "photon";

    /// <inheritdoc />
    public override string DisplayName => "Photon";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Map };

    /// <inheritdoc />
    public override bool SupportsPaging => false;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 1;
    public override double Timeout => 10.0;

    public PhotonSearchEngine() : base() { }
    public PhotonSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _baseUrl + "/api/?q=" + Uri.EscapeDataString(query.Query) + "&limit=" + _limit;

            if (!string.IsNullOrEmpty(query.Language) && query.Language != "all")
            {
                var lang = query.Language.Split('_')[0];
                if (_supportedLanguages.Contains(lang))
                    url += "&lang=" + lang;
            }

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} geocoding results", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var features = doc.RootElement.GetProperty("features");

            foreach (var feature in features.EnumerateArray())
            {
                try
                {
                    if (!feature.TryGetProperty("properties", out var properties))
                        continue;

                    var title = properties.GetProperty("name").GetString() ?? "";
                    if (string.IsNullOrEmpty(title)) continue;

                    // OSM type
                    var osmType = properties.GetProperty("osm_type").GetString() ?? "";
                    var osmTypeStr = osmType switch
                    {
                        "N" => "node",
                        "W" => "way",
                        "R" => "relation",
                        _ => null
                    };
                    if (osmTypeStr == null) continue;

                    var osmId = properties.GetProperty("osm_id").GetInt64();
                    var url = _resultBaseUrl
                        .Replace("{osm_type}", osmTypeStr)
                        .Replace("{osm_id}", osmId.ToString());

                    // Geometry (coordinates)
                    var geometry = feature.GetProperty("geometry");
                    var coords = geometry.GetProperty("coordinates");
                    var lon = coords[0].GetDouble();
                    var lat = coords[1].GetDouble();

                    // Bounding box
                    var boundingbox = new List<double>();
                    if (properties.TryGetProperty("extent", out var extent))
                    {
                        boundingbox.Add(extent[3].GetDouble()); // south
                        boundingbox.Add(extent[1].GetDouble()); // north
                        boundingbox.Add(extent[0].GetDouble()); // west
                        boundingbox.Add(extent[2].GetDouble()); // east
                    }
                    else
                    {
                        boundingbox.Add(lat);
                        boundingbox.Add(lat);
                        boundingbox.Add(lon);
                        boundingbox.Add(lon);
                    }

                    // Address
                    var addressParts = new List<string>();
                    var osmKey = properties.GetProperty("osm_key").GetString() ?? "";

                    if (osmKey is "amenity" or "shop" or "tourism" or "leisure")
                    {
                        var name = properties.GetProperty("name").GetString() ?? "";
                        if (!string.IsNullOrEmpty(name))
                            addressParts.Add(name);
                    }

                    var houseNumber = properties.TryGetProperty("housenumber", out var hn)
                        ? hn.GetString() : null;
                    var street = properties.TryGetProperty("street", out var st)
                        ? st.GetString() : null;
                    var city = properties.TryGetProperty("city", out var ci)
                        ? ci.GetString()
                        : properties.TryGetProperty("town", out var tw)
                            ? tw.GetString()
                            : properties.TryGetProperty("village", out var vi)
                                ? vi.GetString() : null;
                    var postcode = properties.TryGetProperty("postcode", out var pc)
                        ? pc.GetString() : null;
                    var country = properties.TryGetProperty("country", out var co)
                        ? co.GetString() : null;

                    if (!string.IsNullOrEmpty(houseNumber)) addressParts.Add(houseNumber);
                    if (!string.IsNullOrEmpty(street)) addressParts.Add(street);
                    if (!string.IsNullOrEmpty(city)) addressParts.Add(city);
                    if (!string.IsNullOrEmpty(postcode)) addressParts.Add(postcode);
                    if (!string.IsNullOrEmpty(country)) addressParts.Add(country);

                    var address = addressParts.Count > 0 ? string.Join(", ", addressParts) : "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = address,
                        Score = lat,
                        Metadata = $"📍 {lat:F5}, {lon:F5}",
                        Engine = Name,
                        Category = SearchCategory.Map,
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return results;
    }
}
