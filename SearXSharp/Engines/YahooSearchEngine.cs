using AngleSharp;
using SearXSharp.Models;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Yahoo search.
/// Uses HTML scraping of search.yahoo.com.
/// Based on SearXNG's yahoo.py.
/// </summary>
public class YahooSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://search.yahoo.com/search";

    /// <inheritdoc />
    public override string Name => "yahoo";

    /// <inheritdoc />
    public override string DisplayName => "Yahoo";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public YahooSearchEngine() : base() { }
    public YahooSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var b = (query.Page - 1) * 10 + 1;
            var parameters = new Dictionary<string, string>
            {
                ["p"] = query.Query,
                ["b"] = b.ToString(),
                ["vc"] = "",
            };

            var url = _searchUrl + "?" + string.Join("&",
                parameters.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));

            var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseHtml(html, query.Page);
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

    private SearchResultList ParseHtml(string html, int page)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Yahoo results are in <div class="algo-sr"> or similar
            var items = document.QuerySelectorAll("div.algo, div.dd.algo, div[class*='algo']");

            var position = (page - 1) * 10 + 1;
            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("h3 a, a[class*='ac-algo']");
                    if (link == null) continue;

                    var url = link.GetAttribute("href") ?? "";
                    var title = link.TextContent.Trim();
                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                        continue;

                    var contentEl = item.QuerySelector("div.compText, p, span[class*='fc']");
                    var content = contentEl?.TextContent?.Trim() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Web,
                        Position = position++,
                    });
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
