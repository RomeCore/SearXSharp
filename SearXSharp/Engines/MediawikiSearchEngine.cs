using SearXSharp.Models;
using System.Text.Json;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for generic MediaWiki wikis (like Wikipedia).
/// Uses the MediaWiki Action API (no API key required).
/// Based on SearXNG's mediawiki.py.
/// </summary>
public class MediawikiSearchEngine : SearchEngineBase
{
    /// <summary>
    /// Base URL template. Use {language} placeholder for language-specific wikis.
    /// Default: https://{language}.wikipedia.org/
    /// </summary>
    public string BaseUrl { get; set; } = "https://{language}.wikipedia.org/";

    /// <inheritdoc />
    public override string Name => "mediawiki";

    /// <inheritdoc />
    public override string DisplayName => "MediaWiki";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

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

    private string? _currentLanguage;

    public MediawikiSearchEngine() : base() { }
    public MediawikiSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var language = (!string.IsNullOrEmpty(query.Language) && query.Language != "all")
                ? query.Language.Split('-')[0]
                : "en";
            _currentLanguage = language;

            var baseUrl = BaseUrl.Replace("{language}", language).TrimEnd('/');
            var offset = (query.Page - 1) * 5;

            var args = new Dictionary<string, string>
            {
                ["action"] = "query",
                ["list"] = "search",
                ["format"] = "json",
                ["srsearch"] = query.Query,
                ["sroffset"] = offset.ToString(),
                ["srlimit"] = "5",
                ["srwhat"] = "nearmatch",
                ["srprop"] = "snippet|timestamp|categorysnippet",
                ["srsort"] = "relevance",
                ["srenablerewrites"] = "1",
            };

            var url = $"{baseUrl}/w/api.php?{string.Join("&", args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))}";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(json, baseUrl);
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

    private SearchResultList ParseJson(string json, string baseUrl)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("query", out var query)
                || !query.TryGetProperty("search", out var search))
                return CreateResultList(results);

            foreach (var item in search.EnumerateArray())
            {
                try
                {
                    var title = item.GetProperty("title").GetString() ?? "";

                    var snippet = item.TryGetProperty("snippet", out var sn)
                        ? sn.GetString() ?? "" : "";

                    var categorySnippet = item.TryGetProperty("categorysnippet", out var cat)
                        ? cat.GetString() ?? "" : "";

                    // Skip redirects
                    if (snippet.StartsWith("#REDIRECT")) continue;

                    var url = $"{baseUrl}/wiki/{HttpUtility.UrlEncode(title.Replace(' ', '_'))}";

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("timestamp", out var ts))
                    {
                        if (DateTime.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = System.Net.WebUtility.HtmlDecode(snippet),
                        PublishedDate = publishedDate,
                        Metadata = !string.IsNullOrEmpty(categorySnippet)
                            ? System.Net.WebUtility.HtmlDecode(categorySnippet) : null,
                        Engine = Name,
                        Category = SearchCategory.Web,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
