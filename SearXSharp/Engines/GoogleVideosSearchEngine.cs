using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Google Videos.
/// Uses HTML scraping similar to SearXNG's google_videos.py with data:image extraction.
/// </summary>
public class GoogleVideosSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.google.com/search";

    /// <inheritdoc />
    public override string Name => "google videos";

    /// <inheritdoc />
    public override string DisplayName => "Google Videos";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos, SearchCategory.Web };

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

    // Regex to extract data:image objects: "dimg_XXXX"[...];data:image/...;
    private static readonly Regex _dataImageRegex = new(
        "\"(dimg_[^\"]*)\"[^;]*;(data:image[^;]*;[^;]*);?", RegexOptions.Compiled);

    public GoogleVideosSearchEngine() : base() { }
    public GoogleVideosSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var start = (query.Page - 1) * 10;
            var queryParams = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["tbm"] = "vid",
                ["start"] = start.ToString(),
                ["asearch"] = "arc",
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tbs))
                queryParams["tbs"] = "qdr:" + tbs;
            if (query.SafeSearch != SafeSearchLevel.None && _filterMapping.TryGetValue(query.SafeSearch, out var safe))
                queryParams["safe"] = safe;

            var url = _baseUrl + "?" + string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseHtml(html);
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

    private SearchResultList ParseHtml(string html)
    {
        var results = new List<SearchResult>();

        // Extract data:image mappings
        var dataImageMap = new Dictionary<string, string>();
        foreach (Match match in _dataImageRegex.Matches(html))
        {
            var imgId = match.Groups[1].Value;
            var dataImage = match.Groups[2].Value;
            var endPos = dataImage.LastIndexOf('=');
            if (endPos > 0)
                dataImage = dataImage[..(endPos + 1)];
            dataImageMap[imgId] = dataImage;
        }

        var context = BrowsingContext.New(Configuration.Default);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var resultDivs = doc.QuerySelectorAll("div.MjjYud");
        foreach (var result in resultDivs)
        {
            try
            {
                var heading = result.QuerySelector("h3.LC20lb, div[role=\"heading\"]");
                var title = heading?.TextContent?.Trim();
                if (string.IsNullOrEmpty(title)) continue;

                var link = result.QuerySelector("a[jsname=\"UWckNb\"], a[href*=\"/url?q=\"]");
                if (link == null) continue;

                var url = link.GetAttribute("href") ?? "";
                if (url.StartsWith("/url?q="))
                {
                    url = HttpUtility.UrlDecode(url[7..]);
                    var saIndex = url.IndexOf("&sa=U", StringComparison.Ordinal);
                    if (saIndex > 0) url = url[..saIndex];
                }

                var contentDiv = result.QuerySelector("div.ITZIwc");
                var content = contentDiv?.TextContent?.Trim() ?? "";

                var pubInfoEl = result.QuerySelector("div.gqF9jc, div.WRu9Cd");
                var pubInfo = pubInfoEl?.TextContent?.Trim();

                var img = result.QuerySelector("img");
                var thumbnail = img?.GetAttribute("src");

                // Handle data:image thumbnails
                if (thumbnail != null && thumbnail.StartsWith("data:image"))
                {
                    var imgId = img?.GetAttribute("id");
                    if (imgId != null && dataImageMap.TryGetValue(imgId, out var mapped))
                        thumbnail = mapped;
                    else
                        thumbnail = null;
                }

                var durationSpan = result.QuerySelector("span.k1U36b");
                var duration = durationSpan?.TextContent?.Trim();

                var videoDiv = result.QuerySelector("div[rTuANe]");
                var videoId = videoDiv?.GetAttribute("data-vid");

                // Fallback: extract video ID from YouTube URL
                if (videoId == null && url.Contains("youtube.com"))
                {
                    var uri = new Uri(url);
                    var qs = HttpUtility.ParseQueryString(uri.Query);
                    videoId = qs["v"];
                }

                // YouTube thumbnail fallback
                if (thumbnail == null && videoId != null)
                    thumbnail = $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg";

                results.Add(new SearchResult
                {
                    Url = url,
                    Title = title,
                    Content = content,
                    Author = pubInfo,
                    Thumbnail = thumbnail,
                    Duration = ParseDuration(duration),
                    Engine = Name,
                    Category = SearchCategory.Videos,
                    Type = SearchResultType.Video,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "{Engine}: Failed to parse video result", Name);
            }
        }

        return CreateResultList(results);
    }

    private static TimeSpan? ParseDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return null;
        try
        {
            // Format: "12:34" or "1:23:45"
            var parts = duration.Split(':');
            if (parts.Length == 2)
                return TimeSpan.FromMinutes(int.Parse(parts[0])) + TimeSpan.FromSeconds(int.Parse(parts[1]));
            if (parts.Length == 3)
                return TimeSpan.FromHours(int.Parse(parts[0]))
                    + TimeSpan.FromMinutes(int.Parse(parts[1]))
                    + TimeSpan.FromSeconds(int.Parse(parts[2]));
        }
        catch { }
        return null;
    }
}
