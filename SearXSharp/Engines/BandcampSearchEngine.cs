using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Bandcamp (bandcamp.com).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's bandcamp.py.
/// </summary>
public class BandcampSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://bandcamp.com/";
    private const string _iframeSrc = "https://bandcamp.com/EmbeddedPlayer/{0}={1}/size=large/bgcol=000/linkcol=fff/artwork=small";

    /// <inheritdoc />
    public override string Name => "bandcamp";

    /// <inheritdoc />
    public override string DisplayName => "Bandcamp";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Music };

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

    public BandcampSearchEngine() : base() { }
    public BandcampSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var searchPath = $"search?q={Uri.EscapeDataString(query.Query)}&page={query.Page}";
            var url = _baseUrl + searchPath;

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

            var items = document.QuerySelectorAll("li.searchresult");

            foreach (var item in items)
            {
                try
                {
                    // URL
                    var link = item.QuerySelector("div.itemurl a");
                    if (link == null) continue;
                    var url = link.GetAttribute("href") ?? "";

                    // Title
                    var titleEl = item.QuerySelector("div.heading a");
                    var title = titleEl?.TextContent.Trim() ?? "";

                    // Content
                    var contentEl = item.QuerySelector("div.subhead");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    // Published date
                    DateTime? publishedDate = null;
                    var dateEl = item.QuerySelector("div.released");
                    if (dateEl != null)
                    {
                        var dateText = dateEl.TextContent.Trim().Replace("released ", "");
                        if (DateTime.TryParse(dateText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    // Thumbnail
                    var thumbnail = "";
                    var img = item.QuerySelector("div.art img");
                    if (img != null)
                        thumbnail = img.GetAttribute("src") ?? "";

                    // Item type and ID for iframe
                    var itemTypeEl = item.QuerySelector("div.itemtype");
                    var itemType = itemTypeEl?.TextContent.Trim().ToLowerInvariant() ?? "";
                    var itemId = "";
                    if (link.GetAttribute("href") is { } href && href.Contains("search_item_id="))
                    {
                        var parts = href.Split('?');
                        if (parts.Length > 1)
                        {
                            var qs = System.Web.HttpUtility.ParseQueryString(parts[1]);
                            itemId = qs["search_item_id"] ?? "";
                        }
                    }

                    string? iframeSrc = null;

                    // Embed iframe for albums and tracks
                    if (!string.IsNullOrEmpty(itemId))
                    {
                        var type = itemType switch
                        {
                            "album" => "album",
                            "track" => "track",
                            _ => null,
                        };
                        if (type != null)
                            iframeSrc = string.Format(_iframeSrc, type, itemId);
                    }

                    var result = new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                        Engine = Name,
                        Category = SearchCategory.Music,
                        IframeSrc = iframeSrc,
                    };

                    results.Add(result);
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
