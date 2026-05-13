using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Duden (duden.de).
/// German dictionary. Uses HTML scraping.
/// Based on SearXNG's duden.py.
/// </summary>
public class DudenSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.duden.de";

    /// <inheritdoc />
    public override string Name => "duden";

    /// <inheritdoc />
    public override string DisplayName => "Duden";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General };

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

    public DudenSearchEngine() : base() { }
    public DudenSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var offset = query.Page - 1;
            var url = offset == 0
                ? $"{_baseUrl}/suchen/dudenonline/{Uri.EscapeDataString(query.Query)}"
                : $"{_baseUrl}/suchen/dudenonline/{Uri.EscapeDataString(query.Query)}?search_api_fulltext=&page={offset}";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);

            if ((int)response.StatusCode == 404)
                return CreateResultList(new List<SearchResult>());

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
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            var sections = document.QuerySelectorAll("section:not(.essay)");

            foreach (var section in sections)
            {
                try
                {
                    var link = section.QuerySelector("h2 a");
                    if (link == null) continue;

                    var href = link.GetAttribute("href") ?? "";
                    var url = href.StartsWith("http") ? href : _baseUrl + href;
                    var title = link.TextContent.Trim();

                    var p = section.QuerySelector("p");
                    var content = p?.TextContent?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(title))
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
                    _logger.Debug(ex, "{Engine}: Failed to parse section", Name);
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
