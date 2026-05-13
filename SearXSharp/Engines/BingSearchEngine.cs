using AngleSharp;
using AngleSharp.Dom;
using SearXSharp.Models;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Bing (www.bing.com).
/// Uses HTML scraping (non-API) to retrieve search results, similar to SearXNG's bing.py.
/// Supports general web search, news, and respects safe search settings.
/// </summary>
public class BingSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.bing.com/search";
    private const string _newsBaseUrl = "https://www.bing.com/news/infinitescrollajax";

    // Bing safe search parameter mapping (maps SearchXNG-style to Bing's adlt parameter)
    private static readonly Dictionary<SafeSearchLevel, string> _safeSearchMap = new()
    {
        [SafeSearchLevel.None] = "off",
        [SafeSearchLevel.Moderate] = "moderate",
        [SafeSearchLevel.Strict] = "strict",
    };

    /// <inheritdoc />
    public override string Name => "bing";

    /// <inheritdoc />
    public override string DisplayName => "Bing";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.News };

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

    /// <summary>
    /// Initializes a new instance of the Bing search engine.
    /// </summary>
    public BingSearchEngine() : base() { }

    /// <summary>
    /// Initializes a new instance with a logger.
    /// </summary>
    public BingSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
        {
            return CreateErrorResult(validationError);
        }

        try
        {
            if (query.Category == SearchCategory.News)
            {
                return await SearchNewsAsync(query, ct);
            }
            return await SearchWebAsync(query, ct);
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

    /// <summary>
    /// Performs a general web search on Bing.
    /// Based on SearXNG's <c>bing.py request()</c> and <c>response()</c>.
    /// </summary>
    private async Task<SearchResultList> SearchWebAsync(SearchQuery query, CancellationToken ct)
    {
        var start = (query.Page - 1) * 10;

        var parameters = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["adlt"] = _safeSearchMap.GetValueOrDefault(query.SafeSearch, "off"),
            ["first"] = start.ToString(),
        };

        var url = _baseUrl + "?" + string.Join("&", parameters.Select(p =>
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"));

        using var request = CreateGetRequest(url);
        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var results = ParseWebResults(html);

        _logger.Debug("{Engine}: Parsed {Count} web results", Name, results.Count);
        return CreateResultList(results);
    }

    /// <summary>
    /// Performs a news search on Bing.
    /// Uses Bing's infinite scroll AJAX endpoint, similar to SearXNG's bing_news.py.
    /// </summary>
    private async Task<SearchResultList> SearchNewsAsync(SearchQuery query, CancellationToken ct)
    {
        var page = query.Page - 1;
        var parameters = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["InfiniteScroll"] = "1",
            ["first"] = (page * 10 + 1).ToString(),
            ["SFX"] = page.ToString(),
            ["form"] = "PTFTNR",
        };

        var url = _newsBaseUrl + "?" + string.Join("&", parameters.Select(p =>
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"));

        using var request = CreateGetRequest(url);
        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var results = ParseNewsResults(html);

        _logger.Debug("{Engine}: Parsed {Count} news results", Name, results.Count);
        return CreateResultList(results);
    }

    /// <summary>
    /// Parses Bing web search results from HTML.
    /// Based on SearXNG's <c>bing.py response()</c> with XPath selectors converted to CSS selectors.
    /// </summary>
    private List<SearchResult> ParseWebResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Bing results are in <li class="b_algo"> inside <ol id="b_results">
            var items = document.QuerySelectorAll("ol#b_results > li.b_algo");

            var position = (1 - 1) * 10 + 1; // Start position based on page
            foreach (var item in items)
            {
                try
                {
                    var result = ParseSingleWebResult(item, position);
                    if (result != null)
                    {
                        results.Add(result);
                        position++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse a result item", Name);
                }
            }

            // Extract total result count if available
            var countElement = document.QuerySelector("span.sb_count");
            if (countElement != null && long.TryParse(new string(countElement.TextContent.Where(char.IsDigit).ToArray()), out var total))
            {
                _logger.Debug("{Engine}: Total results reported: {Total}", Name, total);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse web results HTML", Name);
        }

        return results;
    }

    /// <summary>
    /// Parses a single Bing web result item.
    /// Handles Bing's URL redirect decoding (base64url-encoded /ck/a?u=a1... links).
    /// Based on SearXNG's bing.py parsing logic.
    /// </summary>
    private static SearchResult? ParseSingleWebResult(IElement item, int position)
    {
        // Extract title and URL from the <h2><a> element
        var linkElement = item.QuerySelector("h2 a");
        if (linkElement == null) return null;

        var title = linkElement.TextContent.Trim();
        var href = linkElement.GetAttribute("href") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) return null;

        // Decode Bing's redirect URL (/ck/a?u=a1base64encodedurl)
        var url = DecodeBingUrl(href);

        // Extract content from <p> elements
        var contentElements = item.QuerySelectorAll("p");
        var content = string.Join(" ", contentElements.Select(p => p.TextContent.Trim())).Trim();

        // Remove decorative icons that Bing injects (like <span class="algoSlug_icon">)
        content = System.Text.RegularExpressions.Regex.Replace(content, @"\s+", " ").Trim();

        return new SearchResult
        {
            Url = url,
            Title = title,
            Content = content,
            Engine = "bing",
            Type = SearchResultType.Default,
            Category = SearchCategory.Web,
            Position = position,
        };
    }

    /// <summary>
    /// Decodes Bing's redirect-protected URLs.
    /// Bing wraps result links as <c>/ck/a?u=a1&lt;base64url&gt;</c>.
    /// Based on SearXNG's bing.py base64url decoding logic.
    /// </summary>
    private static string DecodeBingUrl(string href)
    {
        if (!href.StartsWith("https://www.bing.com/ck/a?"))
        {
            return href;
        }

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(href).Query);
            var uValue = query["u"];
            if (string.IsNullOrEmpty(uValue)) return href;

            if (uValue.StartsWith("a1"))
            {
                var encoded = uValue[2..];
                // Add base64 padding if needed
                encoded += new string('=', (4 - encoded.Length % 4) % 4);
                var bytes = Convert.FromBase64String(encoded);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            // If decoding fails, return the original URL
        }

        return href;
    }

    /// <summary>
    /// Parses Bing news search results from HTML.
    /// Based on SearXNG's <c>bing_news.py response()</c>.
    /// </summary>
    private List<SearchResult> ParseNewsResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var newsItems = document.QuerySelectorAll("div.newsitem");

            var position = (1 - 1) * 10 + 1;
            foreach (var item in newsItems)
            {
                try
                {
                    var link = item.QuerySelector("a.title");
                    if (link == null) continue;

                    var url = link.GetAttribute("href") ?? string.Empty;
                    var title = link.TextContent.Trim();
                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) continue;

                    var content = item.QuerySelector("div.snippet")?.TextContent.Trim() ?? string.Empty;

                    var sourceElement = item.QuerySelector("div.source");
                    var metadata = sourceElement?.QuerySelector("span[aria-label]")?.GetAttribute("aria-label") ?? string.Empty;

                    var imgElement = item.QuerySelector("a.imagelink img");
                    var thumbnail = imgElement?.GetAttribute("src");
                    if (!string.IsNullOrEmpty(thumbnail) && !thumbnail.StartsWith("https://www.bing.com"))
                    {
                        thumbnail = "https://www.bing.com/" + thumbnail;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = "bing",
                        Type = SearchResultType.News,
                        Category = SearchCategory.News,
                        Thumbnail = thumbnail,
                        Metadata = metadata,
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse a news item", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse news results HTML", Name);
        }

        return results;
    }
}
