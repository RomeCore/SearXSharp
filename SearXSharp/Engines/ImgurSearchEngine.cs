using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Imgur (imgur.com).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's imgur.py.
/// </summary>
public class ImgurSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://imgur.com";

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "day",
        [TimeRange.Week] = "week",
        [TimeRange.Month] = "month",
        [TimeRange.Year] = "year",
    };

    /// <inheritdoc />
    public override string Name => "imgur";

    /// <inheritdoc />
    public override string DisplayName => "Imgur";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public ImgurSearchEngine() : base() { }
    public ImgurSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var timeRange = query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tr)
                ? tr : "all";

            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["qs"] = "thumbs",
                ["p"] = (query.Page - 1).ToString(),
            };

            var url = $"{_baseUrl}/search/score/{timeRange}?"
                + string.Join("&", args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
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

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var posts = document.QuerySelectorAll("div.cards div.post");

            foreach (var post in posts)
            {
                try
                {
                    var link = post.QuerySelector("a");
                    if (link == null) continue;

                    var url = link.GetAttribute("href");
                    if (string.IsNullOrEmpty(url)) continue;

                    var img = link.QuerySelector("img");
                    if (img == null) continue;

                    var thumbnailSrc = img.GetAttribute("src") ?? "";
                    var title = img.GetAttribute("alt") ?? "";

                    // Skip if thumbnail is too short (bug at imgur's side)
                    if (thumbnailSrc.Length < 25) continue;

                    // Get full-size image by replacing "b." with "."
                    var imgSrc = thumbnailSrc.Replace("b.", ".");

                    results.Add(new SearchResult
                    {
                        Url = _baseUrl + url,
                        Title = title,
                        ImgSrc = imgSrc,
                        Thumbnail = thumbnailSrc,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return CreateResultList(results);
    }
}
