using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Seznam (seznam.cz).
/// Czech search engine. Uses HTML scraping.
/// Based on SearXNG's seznam.py.
/// </summary>
public class SeznamSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://search.seznam.cz/";

    /// <inheritdoc />
    public override string Name => "seznam";

    /// <inheritdoc />
    public override string DisplayName => "Seznam";

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

    public SeznamSearchEngine() : base() { }
    public SeznamSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _baseUrl + $"?q={Uri.EscapeDataString(query.Query)}";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);

            // Check for verification page
            if (response.RequestMessage?.RequestUri?.AbsolutePath?.StartsWith("/verify") == true)
            {
                _logger.Warning("{Engine}: Access denied by verification page", Name);
                return CreateErrorResult("access_denied");
            }

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

            var items = document.QuerySelectorAll("div#searchpage-root div.Layout--left div.f2c528");

            foreach (var item in items)
            {
                try
                {
                    var titleLink = item.QuerySelector("h3 a");
                    if (titleLink == null) continue;

                    var url = titleLink.GetAttribute("href") ?? "";
                    var title = titleLink.TextContent.Trim();

                    var contentEl = item.QuerySelector("div.c8774a, div.e69e8d.a11657");
                    var content = contentEl?.TextContent?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(title))
                    {
                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = content,
                            Engine = Name,
                            Category = SearchCategory.Web,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result", Name);
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
