using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Wallhaven (wallpaper gallery).
/// Uses Wallhaven's public API v1 (no API key required for basic usage).
/// Based on SearXNG's wallhaven.py.
/// </summary>
public class WallhavenSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://wallhaven.cc/api/v1/search";
    private const string _baseUrl = "https://wallhaven.cc";

    // Maps SearXSharp safesearch -> Wallhaven purity bits
    private static readonly Dictionary<SafeSearchLevel, string> _purityMap = new()
    {
        [SafeSearchLevel.None] = "111",
        [SafeSearchLevel.Moderate] = "110",
        [SafeSearchLevel.Strict] = "100",
    };

    /// <inheritdoc />
    public override string Name => "wallhaven";

    /// <inheritdoc />
    public override string DisplayName => "Wallhaven";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 50;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public WallhavenSearchEngine() : base() { }
    public WallhavenSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["purity"] = _purityMap.GetValueOrDefault(query.SafeSearch, "111"),
            };

            var url = _searchUrl + "?" + string.Join("&",
                args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
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

            if (!root.TryGetProperty("data", out var data))
                return CreateResultList(results);

            foreach (var result in data.EnumerateArray())
            {
                try
                {
                    var url = result.GetProperty("url").GetString() ?? "";
                    var imgSrc = result.GetProperty("path").GetString() ?? "";
                    var thumbnail = result.GetProperty("thumbs").GetProperty("small").GetString() ?? "";
                    var resolution = result.GetProperty("resolution").GetString() ?? "";
                    var category = result.GetProperty("category").GetString() ?? "";
                    var purity = result.GetProperty("purity").GetString() ?? "";
                    var fileType = result.GetProperty("file_type").GetString() ?? "";

                    var fileSize = 0L;
                    if (result.TryGetProperty("file_size", out var fs))
                        fileSize = fs.GetInt64();

                    DateTime? publishedDate = null;
                    if (result.TryGetProperty("created_at", out var created))
                    {
                        if (DateTime.TryParse(created.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    var content = $"{category} / {purity} | {resolution} | {fileType}";
                    if (fileSize > 0)
                    {
                        var sizeStr = fileSize >= 1_000_000
                            ? $"{fileSize / 1_000_000.0:F1} MB"
                            : $"{fileSize / 1_000.0:F1} KB";
                        content += $" | {sizeStr}";
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = $"{resolution} Wallpaper",
                        Content = content,
                        ImgSrc = imgSrc,
                        Thumbnail = thumbnail,
                        Resolution = resolution.Replace("x", " x "),
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse wallpaper", Name);
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
