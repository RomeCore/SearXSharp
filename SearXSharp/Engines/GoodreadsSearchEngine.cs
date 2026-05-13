using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Goodreads (goodreads.com).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's goodreads.py.
/// </summary>
public class GoodreadsSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.goodreads.com";

    /// <inheritdoc />
    public override string Name => "goodreads";

    /// <inheritdoc />
    public override string DisplayName => "Goodreads";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Science, SearchCategory.General };

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

    public GoodreadsSearchEngine() : base() { }
    public GoodreadsSearchEngine(ILogger logger) : base(logger) { }

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

            var url = $"{_baseUrl}/search?" + string.Join("&", args.Select(kv =>
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

            var rows = document.QuerySelectorAll("table tr");

            foreach (var row in rows)
            {
                try
                {
                    var link = row.QuerySelector("a.bookTitle");
                    if (link == null) continue;

                    var href = link.GetAttribute("href") ?? "";
                    var url = href.StartsWith("http") ? href : _baseUrl + href;
                    var title = link.TextContent.Trim();

                    var img = row.QuerySelector("img.bookCover");
                    var thumbnail = img?.GetAttribute("src") ?? "";

                    var infoEl = row.QuerySelector("span.uitext");
                    var content = infoEl?.TextContent.Trim() ?? "";

                    var authorEl = row.QuerySelector("a.authorName");
                    var author = authorEl?.TextContent.Trim() ?? "";

                    if (string.IsNullOrEmpty(title)) continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Author = author,
                        Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                        Engine = Name,
                        Category = SearchCategory.Science,
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
