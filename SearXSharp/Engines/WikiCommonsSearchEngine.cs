using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Wikimedia Commons (commons.wikimedia.org).
/// Uses the MediaWiki query API (no API key required).
/// Based on SearXNG's wikicommons.py.
/// </summary>
public class WikiCommonsSearchEngine : SearchEngineBase
{
    private const string _apiUrl = "https://commons.wikimedia.org/w/api.php";

    /// <inheritdoc />
    public override string Name => "wikicommons";

    /// <inheritdoc />
    public override string DisplayName => "Wikimedia Commons";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images, SearchCategory.Files };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public WikiCommonsSearchEngine() : base() { }
    public WikiCommonsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, string>
            {
                ["format"] = "json",
                ["action"] = "query",
                ["prop"] = "info|imageinfo",
                ["generator"] = "search",
                ["gsrnamespace"] = "6",
                ["gsrlimit"] = "10",
                ["gsroffset"] = (10 * (query.Page - 1)).ToString(),
                ["gsrsearch"] = $"filetype:bitmap|drawing {query.Query}",
                ["iiprop"] = "url|size|mime",
                ["iiurlheight"] = "180",
            };

            var url = _apiUrl + "?" + string.Join("&",
                args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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

            if (!root.TryGetProperty("query", out var query)
                || !query.TryGetProperty("pages", out var pages))
                return CreateResultList(results);

            foreach (var page in pages.EnumerateObject())
            {
                try
                {
                    var item = page.Value;
                    if (!item.TryGetProperty("imageinfo", out var imageInfoArr)
                        || imageInfoArr.GetArrayLength() == 0)
                        continue;

                    var imageInfo = imageInfoArr[0];

                    var titleRaw = item.GetProperty("title").GetString() ?? "";
                    var title = titleRaw.Replace("File:", "").Trim();
                    // Remove extension
                    var lastDot = title.LastIndexOf('.');
                    if (lastDot > 0) title = title[..lastDot];

                    var snippet = item.TryGetProperty("snippet", out var sn)
                        ? sn.GetString() ?? "" : "";

                    var url = imageInfo.GetProperty("descriptionurl").GetString() ?? "";
                    var mediaUrl = imageInfo.GetProperty("url").GetString() ?? "";
                    var mime = imageInfo.GetProperty("mime").GetString() ?? "";
                    var thumbUrl = imageInfo.GetProperty("thumburl").GetString() ?? "";

                    var width = imageInfo.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    var height = imageInfo.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = snippet,
                        ImgSrc = mediaUrl,
                        Thumbnail = thumbUrl,
                        Resolution = width > 0 && height > 0 ? $"{width}x{height}" : null,
                        Metadata = mime,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse page result", Name);
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
