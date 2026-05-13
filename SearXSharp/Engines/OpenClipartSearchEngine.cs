using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for OpenClipart (openclipart.org).
/// OpenClipart is a community-driven collection of free, public domain clip art.
/// Based on SearXNG's openclipart.py.
/// </summary>
public class OpenClipartSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://openclipart.org";

    /// <inheritdoc />
    public override string Name => "openclipart";

    /// <inheritdoc />
    public override string DisplayName => "OpenClipart";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public OpenClipartSearchEngine() : base() { }
    public OpenClipartSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["query"] = query.Query,
                ["p"] = query.Page.ToString(),
            };

            var url = _baseUrl + "/search/?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} clipart results", Name, results.Count);
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

            var items = document.QuerySelectorAll("div.gallery > div.artwork");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a");
                    if (link == null) continue;

                    var href = link.GetAttribute("href") ?? "";
                    var url = href.StartsWith("http") ? href : _baseUrl + href;

                    var img = link.QuerySelector("img");
                    var title = img?.GetAttribute("alt") ?? "";
                    var imgSrc = img?.GetAttribute("src") ?? "";
                    var thumbnail = imgSrc.StartsWith("http") ? imgSrc : _baseUrl + imgSrc;

                    if (string.IsNullOrEmpty(title)) continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        ImgSrc = imgSrc.StartsWith("http") ? imgSrc : _baseUrl + imgSrc,
                        Thumbnail = thumbnail,
                        Engine = Name,
                        Type = SearchResultType.Image,
                        Category = SearchCategory.Images,
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
