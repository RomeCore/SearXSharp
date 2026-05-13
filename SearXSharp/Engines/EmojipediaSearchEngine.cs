using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Emojipedia (emojipedia.org).
/// Emojipedia is an emoji reference website documenting the meaning and usage of emoji characters.
/// Based on SearXNG's emojipedia.py.
/// </summary>
public class EmojipediaSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://emojipedia.org";
    private const string _searchUrl = "https://emojipedia.org/search";

    /// <inheritdoc />
    public override string Name => "emojipedia";

    /// <inheritdoc />
    public override string DisplayName => "Emojipedia";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

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

    public EmojipediaSearchEngine() : base() { }
    public EmojipediaSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _searchUrl + "?q=" + Uri.EscapeDataString(query.Query);

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} emoji results", Name, results.Count);
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

            // Emojipedia search results are in divs with class starting with "EmojisList"
            var items = document.QuerySelectorAll("div[class^='EmojisList'] a");

            foreach (var item in items)
            {
                try
                {
                    var href = item.GetAttribute("href");
                    var title = item.TextContent.Trim();

                    if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(title))
                        continue;

                    var url = href.StartsWith("http") ? href : _baseUrl + href;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = string.Empty,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.General,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse emoji item", Name);
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
