using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Bing News.
/// Uses Bing's infinite scroll AJAX endpoint.
/// Based on SearXNG's bing_news.py.
/// </summary>
public class BingNewsSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.bing.com/news/infinitescrollajax";

    /// <inheritdoc />
    public override string Name => "bing news";

    /// <inheritdoc />
    public override string DisplayName => "Bing News";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.News };

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

    private static readonly Dictionary<TimeRange, string> _timeMap = new()
    {
        [TimeRange.Day] = "interval=\"4\"",
        [TimeRange.Week] = "interval=\"7\"",
        [TimeRange.Month] = "interval=\"9\"",
    };

    public BingNewsSearchEngine() : base() { }
    public BingNewsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var page = query.Page - 1;
            var queryParams = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["InfiniteScroll"] = "1",
                ["first"] = (page * 10 + 1).ToString(),
                ["SFX"] = page.ToString(),
                ["form"] = "PTFTNR",
            };

            if (query.TimeRange.HasValue && _timeMap.TryGetValue(query.TimeRange.Value, out var interval))
                queryParams["qft"] = interval;

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
        var context = BrowsingContext.New(Configuration.Default);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var newsItems = doc.QuerySelectorAll("div.newsitem");
        foreach (var item in newsItems)
        {
            try
            {
                var link = item.QuerySelector("a.title");
                if (link == null) continue;

                var url = link.GetAttribute("href") ?? "";
                var title = link.TextContent?.Trim() ?? "";

                var snippetDiv = item.QuerySelector("div.snippet");
                var content = snippetDiv?.TextContent?.Trim() ?? "";

                // Metadata: source, author
                var metadataParts = new List<string>();
                var sourceDiv = item.QuerySelector("div.source");
                if (sourceDiv != null)
                {
                    var ariaLabel = sourceDiv.QuerySelector("span[aria-label]")?.GetAttribute("aria-label");
                    if (!string.IsNullOrEmpty(ariaLabel)) metadataParts.Add(ariaLabel);

                    var authorAttr = link.GetAttribute("data-author");
                    if (!string.IsNullOrEmpty(authorAttr)) metadataParts.Add(authorAttr);
                }
                var metadata = string.Join(" | ", metadataParts);

                // Thumbnail
                string? thumbnail = null;
                var imgLink = item.QuerySelector("a.imagelink img");
                if (imgLink != null)
                {
                    var src = imgLink.GetAttribute("src");
                    if (src != null)
                    {
                        if (src.StartsWith("https://www.bing.com"))
                            thumbnail = src;
                        else
                            thumbnail = "https://www.bing.com/" + src.TrimStart('/');
                    }
                }

                results.Add(new SearchResult
                {
                    Url = url,
                    Title = title,
                    Content = content,
                    Metadata = metadata,
                    Thumbnail = thumbnail,
                    Engine = Name,
                    Category = SearchCategory.News,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "{Engine}: Failed to parse news item", Name);
            }
        }

        return CreateResultList(results);
    }
}
