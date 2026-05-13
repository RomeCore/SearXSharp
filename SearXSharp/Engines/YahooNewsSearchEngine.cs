using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Yahoo News.
/// Uses HTML scraping of news.search.yahoo.com.
/// Based on SearXNG's yahoo_news.py with "ago" relative date parsing.
/// </summary>
public class YahooNewsSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://news.search.yahoo.com/search";

    private static readonly Regex _agoRegex = new(
        @"(\d+)\s*(year|month|week|day|minute|hour)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, TimeSpan> _agoTimeSpan = new()
    {
        ["minute"] = TimeSpan.FromMinutes(1),
        ["hour"] = TimeSpan.FromHours(1),
        ["day"] = TimeSpan.FromDays(1),
        ["week"] = TimeSpan.FromDays(7),
        ["month"] = TimeSpan.FromDays(30),
        ["year"] = TimeSpan.FromDays(365),
    };

    /// <inheritdoc />
    public override string Name => "yahoo news";

    /// <inheritdoc />
    public override string DisplayName => "Yahoo News";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.News };

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

    public YahooNewsSearchEngine() : base() { }
    public YahooNewsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var offset = (query.Page - 1) * 10 + 1;
            var url = _searchUrl + "?p=" + Uri.EscapeDataString(query.Query)
                + "&b=" + offset;

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
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private SearchResultList ParseHtml(string html)
    {
        var results = new List<SearchResult>();
        var context = BrowsingContext.New(Configuration.Default);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var items = doc.QuerySelectorAll("ol.searchCenterMiddle > li");
        foreach (var item in items)
        {
            try
            {
                var link = item.QuerySelector("h4 a");
                if (link == null) continue;

                var href = link.GetAttribute("href") ?? "";
                var url = ParseYahooUrl(href);
                var title = link.TextContent?.Trim() ?? "";

                var contentEl = item.QuerySelector("p");
                var content = contentEl?.TextContent?.Trim() ?? "";

                var img = item.QuerySelector("img");
                var thumbnail = img?.GetAttribute("data-src");

                // Parse published date from "s-time" span
                var timeSpan = item.QuerySelector("span.s-time");
                DateTime? publishedDate = null;
                if (timeSpan != null)
                {
                    var timeText = timeSpan.TextContent?.Trim();
                    if (!string.IsNullOrEmpty(timeText))
                        publishedDate = ParseRelativeDate(timeText);
                }

                results.Add(new SearchResult
                {
                    Url = url,
                    Title = title,
                    Content = content,
                    Thumbnail = thumbnail,
                    PublishedDate = publishedDate,
                    Engine = Name,
                    Category = SearchCategory.News,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "{Engine}: Failed to parse item", Name);
            }
        }

        // Also look for suggestions
        var suggestions = doc.QuerySelectorAll("div.AlsoTry td")
            .Select(s => s.TextContent?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToList();

        return new SearchResultList
        {
            Results = results,
            Suggestions = suggestions,
        };
    }

    private static string ParseYahooUrl(string href)
    {
        try
        {
            // Yahoo wraps URLs: /l/...?url=...&...
            if (href.Contains("url="))
            {
                var prefix = href.StartsWith("/") ? "" : "/";
                var uri = new Uri("http://dummy" + prefix + href);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                return query["url"] ?? href;
            }
        }
        catch { }
        return href;
    }

    private static DateTime? ParseRelativeDate(string text)
    {
        var match = _agoRegex.Match(text);
        if (match.Success)
        {
            var number = int.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value.ToLowerInvariant();

            if (_agoTimeSpan.TryGetValue(unit, out var delta))
                return DateTime.UtcNow - (delta * number);
        }

        // Try absolute date parsing
        if (DateTime.TryParse(text, out var parsed))
            return parsed;

        return null;
    }
}
