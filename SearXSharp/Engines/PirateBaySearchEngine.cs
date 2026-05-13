using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for The Pirate Bay (thepiratebay.org).
/// Uses apibay.org JSON API (no API key required).
/// Based on SearXNG's piratebay.py.
/// </summary>
public partial class PirateBaySearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://apibay.org/q.php?q={0}&cat={1}";
    private const string _baseUrl = "https://thepiratebay.org/";

    private static readonly string[] _trackers =
    {
        "udp://tracker.coppersurfer.tk:6969/announce",
        "udp://9.rarbg.to:2920/announce",
        "udp://tracker.opentrackr.org:1337",
        "udp://tracker.internetwarriors.net:1337/announce",
        "udp://tracker.leechers-paradise.org:6969/announce",
        "udp://tracker.coppersurfer.tk:6969/announce",
        "udp://tracker.pirateparty.gr:6969/announce",
        "udp://tracker.cyberia.is:6969/announce",
    };

    private static readonly Dictionary<SearchCategory, string> _searchTypes = new()
    {
        [SearchCategory.Files] = "0",
        [SearchCategory.Music] = "100",
        [SearchCategory.Videos] = "200",
    };

    /// <inheritdoc />
    public override string Name => "piratebay";

    /// <inheritdoc />
    public override string DisplayName => "The Pirate Bay";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.Music, SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public PirateBaySearchEngine() : base() { }
    public PirateBaySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var searchType = _searchTypes.GetValueOrDefault(query.Category, "0");
            var url = string.Format(_searchUrl, Uri.EscapeDataString(query.Query), searchType);

            using var request = CreateGetRequest(url);
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

            // Check for "no results"
            if (root.GetArrayLength() > 0 && root[0].TryGetProperty("name", out var firstName))
            {
                if (firstName.GetString() == "No results returned")
                    return CreateResultList(results);
            }

            foreach (var result in root.EnumerateArray())
            {
                try
                {
                    var id = result.GetProperty("id").GetString() ?? "";
                    var name = result.GetProperty("name").GetString() ?? "";
                    var infoHash = result.GetProperty("info_hash").GetString() ?? "";
                    var seeders = result.GetProperty("seeders").GetString() ?? "0";
                    var leechers = result.GetProperty("leechers").GetString() ?? "0";
                    var sizeStr = result.GetProperty("size").GetString() ?? "0";
                    var added = result.GetProperty("added").GetString() ?? "0";

                    // Magnet link
                    var trackers = string.Join("&tr=", _trackers);
                    var magnetLink = $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(name)}&tr={trackers}";

                    // Published date
                    DateTime? publishedDate = null;
                    if (long.TryParse(added, out var unixTime) && unixTime > 0)
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;

                    // Human-readable size
                    var fileSize = HumanizeBytes(long.TryParse(sizeStr, out var sizeBytes) ? sizeBytes : 0);

                    results.Add(new SearchResult
                    {
                        Url = $"{_baseUrl}description.php?id={id}",
                        Title = name,
                        Seed = int.TryParse(seeders, out var s) ? s : 0,
                        Leech = int.TryParse(leechers, out var l) ? l : 0,
                        MagnetLink = magnetLink,
                        PublishedDate = publishedDate,
                        Metadata = fileSize,
                        Engine = Name,
                        Category = SearchCategory.Files,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result", Name);
                }
            }

            // Sort by seeders descending
            results = results.OrderByDescending(r => r.Seed).ToList();
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }

    private static string HumanizeBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KiB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MiB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GiB";
    }
}
