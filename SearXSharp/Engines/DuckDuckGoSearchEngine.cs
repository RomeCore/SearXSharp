using AngleSharp;
using SearXSharp.Models;
using System.Collections.Concurrent;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for DuckDuckGo (html.duckduckgo.com).
/// Uses the no-JS HTML endpoint with POST form data and vqd token handling.
/// Based on SearXNG's <c>duckduckgo.py</c> with its complex bot-detection circumvention.
/// </summary>
public class DuckDuckGoSearchEngine : SearchEngineBase
{
    private const string _ddgUrl = "https://html.duckduckgo.com/html/";
    private const string _ddgLiteUrl = "https://lite.duckduckgo.com/lite/";

    // Static user-agent (vqd token is tied to the UA, so it must be stable per session)
    private static readonly string _httpUserAgent = _userAgents[new Random().Next(_userAgents.Length)];

    // Thread-safe cache for vqd tokens (key: query hash, value: vqd token)
    private static readonly ConcurrentDictionary<string, VqdEntry> _vqdCache = new();

    // Time range mapping (SearXNG style -> DDG style)
    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "d",
        [TimeRange.Week] = "w",
        [TimeRange.Month] = "m",
        [TimeRange.Year] = "y",
    };

    /// <inheritdoc />
    public override string Name => "duckduckgo";

    /// <inheritdoc />
    public override string DisplayName => "DuckDuckGo";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    /// <summary>
    /// Initializes a new instance of the DuckDuckGo search engine.
    /// </summary>
    public DuckDuckGoSearchEngine() : base() { }

    /// <summary>
    /// Initializes a new instance with a logger.
    /// </summary>
    public DuckDuckGoSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            if (query.Query.Length >= 500)
            {
                _logger.Warning("{Engine}: Query too long (>500 chars)", Name);
                return new SearchResultList { Results = Array.Empty<SearchResult>() };
            }

            return await SearchHtmlAsync(query, ct);
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
    /// Performs the search via DDG's no-JS HTML endpoint.
    /// Uses POST form data with optional vqd token for pagination.
    /// Based on SearXNG's <c>duckduckgo.py request()</c>.
    /// </summary>
    private async Task<SearchResultList> SearchHtmlAsync(SearchQuery query, CancellationToken ct)
    {
        // Build form data
        var formData = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["b"] = "", // Empty for first page
        };

        // Add time range filter
        if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var df))
        {
            formData["df"] = df;
        }

        // Get or generate vqd for pagination (page > 1)
        if (query.Page > 1)
        {
            var vqd = GetVqd(query.Query);
            if (string.IsNullOrEmpty(vqd))
            {
                // Need to get vqd from first page first
                _logger.Warning("{Engine}: No vqd token available for pagination on query: {Query}", Name, query.Query);
                return CreateErrorResult("vqd_missing");
            }

            var offset = (query.Page - 1) * 10;
            formData["vqd"] = vqd;
            formData["api"] = "d.js";
            formData["o"] = "json";
            formData["v"] = "l";
            formData["dc"] = (offset + 1).ToString();
            formData["s"] = offset.ToString();
        }

        using var request = CreatePostRequest(_ddgUrl, formData);
        // Override UA with the stable one (vqd is tied to UA)
        request.Headers.UserAgent.TryParseAdd(_httpUserAgent);

        // DDG expects specific headers (like SearXNG sets them)
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Referer", "https://html.duckduckgo.com/");

        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);

        // Try to extract vqd from response for future pagination
        ExtractAndCacheVqd(query.Query, html);

        var results = ParseHtmlResults(html);

        _logger.Debug("{Engine}: Parsed {Count} results for page {Page}", Name, results.Count, query.Page);
        return CreateResultList(results);
    }

    /// <summary>
    /// Parses DDG HTML search results.
    /// Based on SearXNG's <c>duckduckgo.py</c> parsing logic.
    /// </summary>
    private List<SearchResult> ParseHtmlResults(string html)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(html)) return results;

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // DDG-lite uses <table> layout; DDG-html uses <div> layout
            // Try the div-based layout first (html.duckduckgo.com)
            var resultElements = document.QuerySelectorAll("div.result, div.web-result, div[class*='result']");

            // Fallback to table-based layout (lite.duckduckgo.com)
            if (resultElements.Length == 0)
            {
                resultElements = document.QuerySelectorAll("table.result-table tr, tr[class*='result']");
            }

            // Fallback: try to find any links with results
            if (resultElements.Length == 0)
            {
                resultElements = document.QuerySelectorAll("a.result-link, a[class*='result']");
            }

            var position = (1 - 1) * 10 + 1;

            foreach (var element in resultElements)
            {
                try
                {
                    // Find link and title
                    var link = element.QuerySelector("a.result-link, a[rel='nofollow'], a[class*='url']");
                    if (link == null) continue;

                    var url = link.GetAttribute("href");
                    var title = link.TextContent.Trim();
                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) continue;

                    // Find content/snippet
                    var snippet = element.QuerySelector("a.result-snippet, span.result-snippet, div.snippet, p");
                    var content = snippet?.TextContent.Trim() ?? string.Empty;

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
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML results", Name);
        }

        return results;
    }

    /// <summary>
    /// Retrieves cached vqd token for a query.
    /// vqd (Validation Query Digest) is a token that DDG requires for pagination.
    /// Based on SearXNG's <c>duckduckgo.py get_vqd()</c>.
    /// </summary>
    private static string? GetVqd(string query)
    {
        var key = GetVqdCacheKey(query);
        if (_vqdCache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.Expiry)
                return entry.Token;
            _vqdCache.TryRemove(key, out _);
        }
        return null;
    }

    /// <summary>
    /// Extracts vqd token from the HTML response and caches it.
    /// Based on SearXNG's <c>duckduckgo.py set_vqd()</c>.
    /// </summary>
    private static void ExtractAndCacheVqd(string query, string html)
    {
        // vqd is typically in a form field or data attribute
        const string vqdPattern = "vqd='";
        var start = html.IndexOf(vqdPattern, StringComparison.Ordinal);
        if (start < 0)
        {
            // Try alternative pattern
            const string altPattern = "name=\"vqd\" value=\"";
            start = html.IndexOf(altPattern, StringComparison.Ordinal);
            if (start < 0) return;
            start += altPattern.Length;
            var end = html.IndexOf("\"", start, StringComparison.Ordinal);
            if (end < 0) return;
            var vqd = html[start..end];
            if (!string.IsNullOrEmpty(vqd))
                CacheVqd(query, vqd);
            return;
        }

        start += vqdPattern.Length;
        var endPos = html.IndexOf("'", start, StringComparison.Ordinal);
        if (endPos < 0) return;

        var vqdToken = html[start..endPos];
        if (!string.IsNullOrEmpty(vqdToken))
            CacheVqd(query, vqdToken);
    }

    /// <summary>
    /// Caches a vqd token for a query with 1-hour expiry.
    /// </summary>
    private static void CacheVqd(string query, string vqd)
    {
        var key = GetVqdCacheKey(query);
        _vqdCache[key] = new VqdEntry(vqd, DateTime.UtcNow.AddHours(1));
    }

    /// <summary>
    /// Generates a cache key for the vqd token (query + user-agent).
    /// </summary>
    private static string GetVqdCacheKey(string query)
    {
        // vqd is tied to both the query and the user-agent
        return $"{query}//{_httpUserAgent}";
    }

    /// <summary>
    /// Vqd cache entry with expiry.
    /// </summary>
    private readonly record struct VqdEntry(string Token, DateTime Expiry);
}
