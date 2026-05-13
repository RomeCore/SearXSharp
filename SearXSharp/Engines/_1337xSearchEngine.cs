using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for 1337x (1337x.to).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's 1337x.py.
/// </summary>
public class _1337xSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://1337x.to";
    private const string _searchUrl = "https://1337x.to/search/{0}/{1}/";

    /// <inheritdoc />
    public override string Name => "1337x";

    /// <inheritdoc />
    public override string DisplayName => "1337x";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files };

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

    public _1337xSearchEngine() : base() { }
    public _1337xSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var url = string.Format(_searchUrl, Uri.EscapeDataString(query.Query), query.Page);

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

            var rows = document.QuerySelectorAll("table.table-list tbody tr");

            foreach (var row in rows)
            {
                try
                {
                    var nameCell = row.QuerySelector("td.name");
                    if (nameCell == null) continue;

                    var links = nameCell.QuerySelectorAll("a");
                    var link = links.Length >= 2 ? links[1] : null;
                    if (link == null) continue;

                    var href = link.GetAttribute("href") ?? "";
                    var fullUrl = href.StartsWith("http") ? href : _baseUrl + href;
                    var title = link.TextContent.Trim();

                    var seedEl = row.QuerySelector("td.seeds");
                    var seed = seedEl?.TextContent.Trim() ?? "0";

                    var leechEl = row.QuerySelector("td.leeches");
                    var leech = leechEl?.TextContent.Trim() ?? "0";

                    var sizeEl = row.QuerySelector("td.size");
                    var size = sizeEl?.TextContent.Trim() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = fullUrl,
                        Title = title,
                        Seed = int.TryParse(seed, out var s) ? s : 0,
                        Leech = int.TryParse(leech, out var l) ? l : 0,
                        Metadata = size,
                        Engine = Name,
                        Category = SearchCategory.Files,
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
