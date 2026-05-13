using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Anna's Archive (annas-archive.org).
/// Anna's Archive is a free non-profit online shadow library metasearch engine
/// providing access to a variety of book resources.
/// Based on SearXNG's annas_archive.py.
/// </summary>
public class AnnasArchiveSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://annas-archive.org";

    /// <inheritdoc />
    public override string Name => "annasarchive";

    /// <inheritdoc />
    public override string DisplayName => "Anna's Archive";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.General };

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

    public AnnasArchiveSearchEngine() : base() { }
    public AnnasArchiveSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["lang"] = query.Language ?? "en",
            };

            var url = _baseUrl + "/search?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} results from Anna's Archive", Name, results.Count);
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

            // Results are in divs with class "flex" inside the "js-aarecord-list-outer" container
            var container = document.QuerySelector("div.js-aarecord-list-outer");
            if (container == null) return results;

            var items = container.QuerySelectorAll(":scope > div.flex");

            foreach (var item in items)
            {
                try
                {
                    // Get the link to the result page (first direct child <a>)
                    var mainLink = item.QuerySelector(":scope > a");
                    var href = mainLink?.GetAttribute("href");
                    if (string.IsNullOrEmpty(href)) continue;

                    var url = href.StartsWith("http") ? href : _baseUrl + href;

                    // Title (link with class "js-vim-focus")
                    var titleLink = item.QuerySelector("a.js-vim-focus");
                    var title = titleLink?.TextContent.Trim();
                    if (string.IsNullOrEmpty(title)) continue;

                    // Content/description (div with class "relative" > div with class "line-clamp")
                    var contentEl = item.QuerySelector("div.relative div[class*='line-clamp']");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    // Thumbnail
                    var img = item.QuerySelector("img");
                    var thumbnail = img?.GetAttribute("src");

                    // Author (identified by "mdi-user-edit" icon)
                    var authorLink = item.QuerySelector("a:has(span[class*='user-edit'])");
                    var author = authorLink?.TextContent.Trim();

                    // Publisher (identified by "mdi-company" icon)
                    var publisherLink = item.QuerySelector("a:has(span[class*='company'])");
                    var publisher = publisherLink?.TextContent.Trim();

                    // Tags (div with "font-semibold" class)
                    var tagsEl = item.QuerySelector("div.font-semibold");
                    var tagsText = tagsEl?.TextContent.Trim() ?? "";
                    var tags = new List<string>();
                    if (!string.IsNullOrEmpty(tagsText))
                    {
                        // Split by "·" and take first part before "Save"
                        var cleanTags = tagsText.Split("Save")[0];
                        tags = cleanTags.Split('·')
                            .Select(t => t.Trim())
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList();
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Thumbnail = thumbnail,
                        Author = author,
                        Source = publisher,
                        Tags = tags,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Files,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse Anna's Archive item", Name);
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
