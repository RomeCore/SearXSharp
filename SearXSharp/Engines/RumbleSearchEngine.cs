using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Rumble (rumble.com).
/// Uses HTML scraping of the search results page.
/// Based on SearXNG's rumble.py.
/// </summary>
public class RumbleSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://rumble.com/";

    /// <inheritdoc />
    public override string Name => "rumble";

    /// <inheritdoc />
    public override string DisplayName => "Rumble";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public RumbleSearchEngine() : base() { }
    public RumbleSearchEngine(ILogger logger) : base(logger) { }

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
                ["q"] = query.Query,
            };
            if (query.Page > 1)
                args["page"] = query.Page.ToString();

            var url = _baseUrl + "search/video?" + string.Join("&",
                args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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

            var items = document.QuerySelectorAll("li.video-listing-entry");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a.video-item--a");
                    if (link == null) continue;

                    var href = link.GetAttribute("href") ?? "";
                    if (string.IsNullOrEmpty(href)) continue;
                    var url = _baseUrl.TrimEnd('/') + href;

                    var img = item.QuerySelector("img.video-item--img");
                    var thumbnail = img?.GetAttribute("src") ?? "";

                    var titleEl = item.QuerySelector("h3.video-item--title");
                    var title = titleEl?.TextContent?.Trim() ?? "";

                    var timeEl = item.QuerySelector("time.video-item--meta.video-item--time");
                    DateTime? publishedDate = null;
                    if (timeEl != null)
                    {
                        var dateStr = timeEl.GetAttribute("datetime");
                        if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
                            publishedDate = dt;
                    }

                    var viewsEl = item.QuerySelector("span.video-item--meta.video-item--views");
                    var views = viewsEl?.GetAttribute("data-value") ?? "";

                    var rumblesEl = item.QuerySelector("span.video-item--meta.video-item--rumbles");
                    var rumbles = rumblesEl?.GetAttribute("data-value") ?? "";

                    var earnedEl = item.QuerySelector("span.video-item--meta.video-item--earned");
                    var earned = earnedEl?.GetAttribute("data-value") ?? "";

                    var authorEl = item.QuerySelector("div.ellipsis-1");
                    var author = authorEl?.TextContent?.Trim() ?? "";

                    var durationEl = item.QuerySelector("span.video-item--duration");
                    var duration = durationEl?.GetAttribute("data-value") ?? "";

                    TimeSpan? parsedDuration = null;
                    if (!string.IsNullOrEmpty(duration) && int.TryParse(duration, out var seconds))
                        parsedDuration = TimeSpan.FromSeconds(seconds);

                    var content = !string.IsNullOrEmpty(earned)
                        ? $"{views} views - {rumbles} rumbles - ${earned}"
                        : $"{views} views - {rumbles} rumbles";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Author = author,
                        Thumbnail = thumbnail,
                        PublishedDate = publishedDate,
                        Duration = parsedDuration,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result item", Name);
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
