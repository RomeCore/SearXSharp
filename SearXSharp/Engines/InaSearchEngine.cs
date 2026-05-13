using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for INA (ina.fr) - French national audiovisual institute.
/// Searches French TV, radio, and multimedia archives.
/// Based on SearXNG's ina.py.
/// </summary>
public class InaSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.ina.fr";
    private const string _searchUrl = "https://www.ina.fr/ajax/recherche";
    private const int _pageSize = 12;

    /// <inheritdoc />
    public override string Name => "ina";

    /// <inheritdoc />
    public override string DisplayName => "INA";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 15.0;

    public InaSearchEngine() : base() { }
    public InaSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var start = query.Page * _pageSize;
            var url = _searchUrl + "?q=" + Uri.EscapeDataString(query.Query)
                + "&espace=1&sort=pertinence&order=desc&offset=" + start + "&modified=size";

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.ina.fr/");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} INA results", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            var items = document.QuerySelectorAll("#searchHits > div");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a");
                    var href = link?.GetAttribute("href") ?? "";
                    var url = href.StartsWith("http") ? href : _baseUrl + href;

                    var titleEl = item.QuerySelector("div[class*='title-bloc-small']");
                    var title = titleEl?.TextContent.Trim() ?? "";

                    var img = item.QuerySelector("img");
                    var thumbnail = img?.GetAttribute("data-src");

                    var dateEl = item.QuerySelector("div[class*='dateAgenda']");
                    var subEl = item.QuerySelector("div[class*='sous-titre-fonction']");
                    var content = (dateEl?.TextContent.Trim() ?? "") + " " + (subEl?.TextContent.Trim() ?? "");

                    if (string.IsNullOrEmpty(title)) continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = System.Net.WebUtility.HtmlDecode(title),
                        Content = content.Trim(),
                        Thumbnail = thumbnail,
                        Engine = Name,
                        Type = SearchResultType.Video,
                        Category = SearchCategory.Videos,
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }
}
