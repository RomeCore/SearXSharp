using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for pkg.go.dev — the official Go package documentation site.
/// Searches for Go packages and modules.
/// Based on SearXNG's pkg_go_dev.py.
/// </summary>
public partial class PkgGoDevSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://pkg.go.dev";
    private const int _maxResults = 50;

    /// <inheritdoc />
    public override string Name => "pkg_go_dev";

    /// <inheritdoc />
    public override string DisplayName => "pkg.go.dev";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Packages };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public PkgGoDevSearchEngine() : base() { }
    public PkgGoDevSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["m"] = "package",
                ["limit"] = _maxResults.ToString(),
            };

            var url = _baseUrl + "/search?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} Go package results", Name, results.Count);
            return CreateResultList(results);
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

    private List<SearchResult> ParseHtml(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Each result is in a div with class "SearchSnippet" inside the search results container
            var searchResults = document.QuerySelectorAll("div.SearchSnippet");

            foreach (var snippet in searchResults)
            {
                try
                {
                    // Title & URL
                    var headerContainer = snippet.QuerySelector("div.SearchSnippet-headerContainer");
                    var link = headerContainer?.QuerySelector("h2 a");
                    if (link == null) continue;

                    var href = link.GetAttribute("href");
                    var title = link.TextContent.Trim();
                    if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(title)) continue;

                    var url = _baseUrl + href;

                    // Package name (in parentheses)
                    var packageSpan = link.QuerySelector("span");
                    var packageName = packageSpan?.TextContent.Trim().Trim('(', ')') ?? "";

                    // Remove package name from title
                    var cleanTitle = title.Replace($"({packageName})", "").Trim();

                    // Version
                    var infoLabel = snippet.QuerySelector("div.SearchSnippet-infoLabel");
                    var versionSpan = infoLabel?.QuerySelector("span strong");
                    var version = versionSpan?.TextContent.Trim() ?? "";

                    // Updated date
                    var publishedSpan = infoLabel?.QuerySelector("span[data-test-id='snippet-published'] strong");
                    var publishedStr = publishedSpan?.TextContent.Trim() ?? "";

                    DateTime? publishedDate = null;
                    if (!string.IsNullOrEmpty(publishedStr))
                    {
                        // Try common date formats
                        if (DateTime.TryParse(publishedStr, out var dt))
                            publishedDate = dt;
                    }

                    // Synopsis / content
                    var synopsis = snippet.QuerySelector("p.SearchSnippet-synopsis");
                    var content = synopsis?.TextContent.Trim() ?? "";

                    // Popularity
                    var popularityEl = infoLabel?.QuerySelector("a strong");
                    var popularityStr = popularityEl?.TextContent.Trim() ?? "0";

                    // License
                    var licenseSpan = infoLabel?.QuerySelector("span[data-test-id='snippet-license'] a");
                    var licenseName = licenseSpan?.TextContent.Trim() ?? "";
                    var licenseUrl = licenseSpan?.GetAttribute("href") ?? "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = cleanTitle,
                        Content = content,
                        PublishedDate = publishedDate,
                        Metadata = $"v{version}",
                        Tags = new List<string>(),
                        Author = packageName,
                        Source = licenseName,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse package snippet", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }
}
