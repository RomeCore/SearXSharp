using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Sogou (sogou.com).
/// Chinese search engine. Uses HTML scraping.
/// Based on SearXNG's sogou.py.
/// </summary>
public partial class SogouSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.sogou.com";

    private static readonly Dictionary<TimeRange, string> _timeRangeDict = new()
    {
        [TimeRange.Day] = "inttime_day",
        [TimeRange.Week] = "inttime_week",
        [TimeRange.Month] = "inttime_month",
        [TimeRange.Year] = "inttime_year",
    };

    [GeneratedRegex(@"(\d{4}-\d{1,2}-\d{1,2})")]
    private static partial Regex DateRegex();

    /// <inheritdoc />
    public override string Name => "sogou";

    /// <inheritdoc />
    public override string DisplayName => "Sogou";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

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

    public SogouSearchEngine() : base() { }
    public SogouSearchEngine(ILogger logger) : base(logger) { }

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
                ["query"] = query.Query,
                ["page"] = query.Page.ToString(),
            };

            if (query.TimeRange.HasValue && _timeRangeDict.TryGetValue(query.TimeRange.Value, out var timeRange))
            {
                queryParams["s_from"] = timeRange;
                queryParams["tsn"] = "1";
            }

            var url = $"{_baseUrl}/web?{string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))}";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);

            // Check for anti-spider/captcha redirect
            if ((int)response.StatusCode == 302)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                if (location.Contains("antispider"))
                {
                    _logger.Warning("{Engine}: Anti-spider challenge triggered", Name);
                    return CreateErrorResult("captcha");
                }
            }

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

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var items = document.QuerySelectorAll("div.rb, div.vrwrap");

            foreach (var item in items)
            {
                try
                {
                    var titleLink = item.QuerySelector("h3.pt a, h3.vr-title a");
                    if (titleLink == null) continue;

                    var title = titleLink.TextContent.Trim();
                    var href = titleLink.GetAttribute("href") ?? "";

                    var url = href;
                    if (url.StartsWith("/link?url="))
                    {
                        // Try to extract real URL from data-url attribute
                        var html2 = item.InnerHtml;
                        var dataUrlMatch = Regex.Match(html2, @"data-url=""([^""]+)""");
                        url = dataUrlMatch.Success ? dataUrlMatch.Groups[1].Value : $"{_baseUrl}{href}";
                    }

                    var contentEl = item.QuerySelector("div.ft, div.attribute-centent, div.fz-mid.space-txt");
                    var content = contentEl?.TextContent?.Trim() ?? "";

                    // Try to get thumbnail
                    var img = item.QuerySelector("div.img-layout img");
                    var thumbnail = img?.GetAttribute("src")?.Replace("http://", "https://");

                    // Try to parse date
                    DateTime? publishedDate = null;
                    var citeEl = item.QuerySelector("cite, span.cite-date");
                    if (citeEl != null)
                    {
                        var dateMatch = DateRegex().Match(citeEl.TextContent);
                        if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups[1].Value, out var dt))
                            publishedDate = dt;
                    }

                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
                    {
                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = content,
                            PublishedDate = publishedDate,
                            Thumbnail = thumbnail,
                            Engine = Name,
                            Category = SearchCategory.Web,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result", Name);
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
