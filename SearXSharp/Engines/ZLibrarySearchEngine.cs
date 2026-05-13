using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Z-Library (zlibrary-global.se).
/// Uses HTML scraping of the search results page.
/// Based on SearXNG's zlibrary.py.
/// </summary>
public class ZLibrarySearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://zlibrary-global.se";

    /// <inheritdoc />
    public override string Name => "zlibrary";

    /// <inheritdoc />
    public override string DisplayName => "Z-Library";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.Science };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    public ZLibrarySearchEngine() : base() { }
    public ZLibrarySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = $"{_baseUrl}/s/{Uri.EscapeDataString(query.Query)}/?page={query.Page}";

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
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Check if domain is seized
            var titleEl = document.QuerySelector("title");
            if (titleEl != null && titleEl.TextContent.ToLower().Contains("seized"))
            {
                _logger.Warning("{Engine}: Domain appears to be seized", Name);
                return CreateResultList(results);
            }

            var items = document.QuerySelectorAll("div#searchResultBox div[class*='resItemBox']");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a[href^='/book/']");
                    if (link == null) continue;
                    var href = link.GetAttribute("href") ?? "";
                    var url = _baseUrl + href;

                    var titleEl2 = item.QuerySelector("[itemprop='name']");
                    var title = titleEl2?.TextContent?.Trim() ?? "";

                    var authorEls = item.QuerySelectorAll("div.authors a[itemprop='author']");
                    var authors = authorEls.Select(a => a.TextContent.Trim()).ToList();

                    var thumbnailEl = item.QuerySelector("img[class*='cover']");
                    var thumbnail = thumbnailEl?.GetAttribute("data-src");
                    if (!string.IsNullOrEmpty(thumbnail) && thumbnail.StartsWith("/"))
                        thumbnail = _baseUrl + thumbnail;

                    var yearEl = item.QuerySelector("div.property_year div.property_value");
                    DateTime? publishedDate = null;
                    if (yearEl != null && int.TryParse(yearEl.TextContent.Trim(), out var year))
                    {
                        try { publishedDate = new DateTime(year, 1, 1); } catch { }
                    }

                    var typeEl = item.QuerySelector("div.property__file div.property_value");
                    var fileType = typeEl?.TextContent?.Trim() ?? "";

                    var publisherEl = item.QuerySelector("a[title='Publisher']");
                    var publisher = publisherEl?.TextContent?.Trim() ?? "";

                    var contentParts = new List<string>();
                    if (!string.IsNullOrEmpty(fileType)) contentParts.Add(fileType);
                    if (!string.IsNullOrEmpty(publisher)) contentParts.Add($"Publisher: {publisher}");

                    if (authors.Count > 0)
                        contentParts.Insert(0, $"By: {string.Join(", ", authors)}");

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = string.Join(" | ", contentParts),
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        PublishedDate = publishedDate,
                        Author = authors.Count > 0 ? authors[0] : null,
                        Engine = Name,
                        Category = SearchCategory.Files,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse book item", Name);
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
