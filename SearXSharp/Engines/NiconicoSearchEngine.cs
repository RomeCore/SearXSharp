using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Niconico (nicovideo.jp).
/// Uses HTML scraping of the search results page.
/// Based on SearXNG's niconico.py.
/// </summary>
public class NiconicoSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.nicovideo.jp";
    private const string _embedUrl = "https://embed.nicovideo.jp";

    private static readonly Dictionary<TimeRange, int> _timeRangeDict = new()
    {
        [TimeRange.Day] = 1,
        [TimeRange.Week] = 7,
        [TimeRange.Month] = 30,
        [TimeRange.Year] = 365,
    };

    /// <inheritdoc />
    public override string Name => "niconico";

    /// <inheritdoc />
    public override string DisplayName => "Niconico";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public NiconicoSearchEngine() : base() { }
    public NiconicoSearchEngine(ILogger logger) : base(logger) { }

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
                ["page"] = query.Page.ToString(),
            };

            if (query.TimeRange.HasValue && _timeRangeDict.TryGetValue(query.TimeRange.Value, out var days))
            {
                var startDate = DateTime.UtcNow.AddDays(-days);
                queryParams["start"] = startDate.ToString("yyyy-MM-dd");
            }

            var url = $"{_baseUrl}/search/{Uri.EscapeDataString(query.Query)}?" +
                string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var items = document.QuerySelectorAll("li[data-video-item]");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a.itemThumbWrap");
                    if (link == null) continue;

                    var href = link.GetAttribute("href") ?? "";
                    var videoId = href.Split('?')[0].Split('/').LastOrDefault() ?? "";

                    if (string.IsNullOrEmpty(videoId)) continue;

                    var url = $"{_baseUrl}/watch/{videoId}";
                    var iframeSrc = $"{_embedUrl}/watch/{videoId}";

                    var titleEl = item.QuerySelector("p.itemTitle a");
                    var title = titleEl?.TextContent?.Trim() ?? "";

                    var descEl = item.QuerySelector("p.itemDescription");
                    var content = descEl?.GetAttribute("title") ?? "";

                    var img = item.QuerySelector("img.thumb");
                    var thumbnail = img?.GetAttribute("src") ?? "";

                    var lengthEl = item.QuerySelector("span.videoLength");
                    TimeSpan? duration = null;
                    if (lengthEl != null)
                    {
                        var lengthStr = lengthEl.TextContent.Trim();
                        if (TimeSpan.TryParse($"00:{lengthStr}", System.Globalization.CultureInfo.InvariantCulture, out var ts))
                            duration = ts;
                    }

                    var timeEl = item.QuerySelector("p.itemTime span.time");
                    DateTime? publishedDate = null;
                    if (timeEl != null)
                    {
                        var timeStr = timeEl.TextContent.Trim();
                        if (DateTime.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture, out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        IframeSrc = iframeSrc,
                        Duration = duration,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse video item", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return CreateResultList(results);
    }
}
