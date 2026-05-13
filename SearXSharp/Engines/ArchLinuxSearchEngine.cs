using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for the Arch Linux Wiki (wiki.archlinux.org).
/// Arch Linux is a Linux-based operating system and its wiki is a comprehensive documentation resource.
/// Based on SearXNG's archlinux.py.
/// </summary>
public class ArchLinuxSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://wiki.archlinux.org";
    private const string _searchUrl = "https://wiki.archlinux.org/index.php";

    /// <inheritdoc />
    public override string Name => "archlinux";

    /// <inheritdoc />
    public override string DisplayName => "Arch Linux Wiki";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Web };

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

    public ArchLinuxSearchEngine() : base() { }
    public ArchLinuxSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var offset = (query.Page - 1) * 20;

            var args = new Dictionary<string, string>
            {
                ["search"] = query.Query,
                ["title"] = "Special:Search",
                ["limit"] = "20",
                ["offset"] = offset.ToString(),
                ["profile"] = "default",
            };

            var url = _searchUrl + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} Arch Wiki results", Name, results.Count);
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

            // Results are in <ul class="mw-search-results">
            var items = document.QuerySelectorAll("ul.mw-search-results > li");

            foreach (var item in items)
            {
                try
                {
                    var heading = item.QuerySelector("div.mw-search-result-heading a");
                    if (heading == null) continue;

                    var href = heading.GetAttribute("href");
                    var title = heading.TextContent.Trim();

                    if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(title))
                        continue;

                    var url = href.StartsWith("http") ? href : _baseUrl + href;

                    var contentEl = item.QuerySelector("div.searchresult");
                    var content = contentEl?.TextContent.Trim() ?? string.Empty;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.IT,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse Arch Wiki result", Name);
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
