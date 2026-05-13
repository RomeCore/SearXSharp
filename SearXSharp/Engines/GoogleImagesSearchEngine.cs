using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Google Images.
/// Uses Google's internal async API (JSON format) similar to SearXNG's google_images.py.
/// Uses GSA (Google Search App) user-agent for better results.
/// </summary>
public class GoogleImagesSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.google.com/search";

    /// <inheritdoc />
    public override string Name => "google images";

    /// <inheritdoc />
    public override string DisplayName => "Google Images";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 50;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    private static readonly Dictionary<SafeSearchLevel, string> _filterMapping = new()
    {
        [SafeSearchLevel.None] = "images",
        [SafeSearchLevel.Moderate] = "active",
        [SafeSearchLevel.Strict] = "active",
    };

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "d",
        [TimeRange.Week] = "w",
        [TimeRange.Month] = "m",
        [TimeRange.Year] = "y",
    };

    public GoogleImagesSearchEngine() : base() { }
    public GoogleImagesSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["tbm"] = "isch",
                ["asearch"] = "isch",
            };

            // Async pagination: _fmt:json, p:1, ijn:{page-1}
            var asyncParam = $"_fmt:json,p:1,ijn:{query.Page - 1}";

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tbs))
                queryParams["tbs"] = "qdr:" + tbs;
            if (query.SafeSearch != SafeSearchLevel.None && _filterMapping.TryGetValue(query.SafeSearch, out var safe))
                queryParams["safe"] = safe;

            var url = _baseUrl + "?" + string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
                + "&async=" + Uri.EscapeDataString(asyncParam);

            // Use GSA user-agent for better results (more results per page)
            var request = CreateGetRequest(url, useGsaAgent: true);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseJsonResponse(html);
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

    private SearchResultList ParseJsonResponse(string html)
    {
        var results = new List<SearchResult>();

        // Find JSON start marker: {"ischj":
        var jsonStart = html.IndexOf("{\"ischj\":", StringComparison.Ordinal);
        if (jsonStart < 0)
            return CreateResultList(results);

        var jsonText = html[jsonStart..];

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ischj", out var ischj))
                return CreateResultList(results);

            if (!ischj.TryGetProperty("metadata", out var metadata))
                return CreateResultList(results);

            foreach (var item in metadata.EnumerateArray())
            {
                try
                {
                    var result = item.GetProperty("result");
                    var referrerUrl = result.GetProperty("referrer_url").GetString() ?? "";
                    var pageTitle = result.GetProperty("page_title").GetString() ?? "";

                    var snippet = "";
                    if (item.TryGetProperty("text_in_grid", out var textInGrid)
                        && textInGrid.TryGetProperty("snippet", out var snippetEl))
                        snippet = snippetEl.GetString() ?? "";

                    var siteTitle = "";
                    if (result.TryGetProperty("site_title", out var siteTitleEl))
                        siteTitle = siteTitleEl.GetString() ?? "";

                    var resolution = "";
                    string? imgSrc = null;
                    string? thumbnailSrc = null;

                    if (item.TryGetProperty("original_image", out var origImg))
                    {
                        var width = origImg.GetProperty("width").GetInt32();
                        var height = origImg.GetProperty("height").GetInt32();
                        resolution = $"{width} x {height}";
                        imgSrc = origImg.GetProperty("url").GetString();
                    }

                    if (item.TryGetProperty("thumbnail", out var thumb)
                        && thumb.TryGetProperty("url", out var thumbUrl))
                        thumbnailSrc = thumbUrl.GetString();

                    // Author from IPTC metadata
                    string? author = null;
                    if (result.TryGetProperty("iptc", out var iptc)
                        && iptc.TryGetProperty("creator", out var creator))
                    {
                        author = string.Join(", ", creator.EnumerateArray().Select(c => c.GetString()));
                    }

                    results.Add(new SearchResult
                    {
                        Url = referrerUrl,
                        Title = pageTitle,
                        Content = snippet,
                        Source = siteTitle,
                        Resolution = resolution,
                        ImgSrc = imgSrc,
                        Thumbnail = thumbnailSrc,
                        Author = author,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse image result item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON response", Name);
        }

        return CreateResultList(results);
    }
}
