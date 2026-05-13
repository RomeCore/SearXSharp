using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for PyPI (pypi.org).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's pypi.py.
/// </summary>
public class PypiSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://pypi.org";
    private const string _searchUrl = "https://pypi.org/search/?";

    /// <inheritdoc />
    public override string Name => "pypi";

    /// <inheritdoc />
    public override string DisplayName => "PyPI";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Packages };

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

    public PypiSearchEngine() : base() { }
    public PypiSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["page"] = query.Page.ToString(),
            };

            var url = _searchUrl + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var entries = document.QuerySelectorAll("a.package-snippet");

            foreach (var entry in entries)
            {
                try
                {
                    var href = entry.GetAttribute("href") ?? "";
                    var url = _baseUrl + href;

                    var nameEl = entry.QuerySelector("span.package-snippet__name");
                    var title = nameEl?.TextContent.Trim() ?? "";

                    var versionEl = entry.QuerySelector("span.package-snippet__version");
                    var version = versionEl?.TextContent.Trim() ?? "";

                    var timeEl = entry.QuerySelector("span.package-snippet__created time");
                    var createdStr = timeEl?.GetAttribute("datetime") ?? "";

                    var contentEl = entry.QuerySelector("p");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    DateTime? publishedDate = null;
                    if (!string.IsNullOrEmpty(createdStr))
                    {
                        if (DateTime.TryParse(createdStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Metadata = $"v{version}",
                        Engine = Name,
                        Category = SearchCategory.Packages,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return CreateResultList(results);
    }
}
