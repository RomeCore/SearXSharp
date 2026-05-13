using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for DeviantArt (deviantart.com).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's deviantart.py.
/// </summary>
public class DeviantArtSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.deviantart.com";

    /// <inheritdoc />
    public override string Name => "deviantart";

    /// <inheritdoc />
    public override string DisplayName => "DeviantArt";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

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

    // Store next page URL for pagination
    private string? _nextPageUrl;

    public DeviantArtSearchEngine() : base() { }
    public DeviantArtSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            string url;
            if (query.Page > 1 && !string.IsNullOrEmpty(_nextPageUrl))
                url = _nextPageUrl;
            else
                url = $"{_baseUrl}/search?q={Uri.EscapeDataString(query.Query)}";

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

            // Find result links
            var items = document.QuerySelectorAll("div.V_S0t_ div div a");

            foreach (var item in items)
            {
                try
                {
                    var url = item.GetAttribute("href") ?? "";
                    var ariaLabel = item.GetAttribute("aria-label") ?? "";

                    var img = item.QuerySelector("div img");
                    var thumbnailSrc = img?.GetAttribute("src") ?? "";
                    var srcset = img?.GetAttribute("srcset") ?? "";

                    // Check for premium content (blurred images)
                    var premiumText = item.ParentElement?.QuerySelector("div div div")?.TextContent ?? "";
                    if (premiumText.Contains("Watch the artist to view this deviation"))
                        continue;

                    // Get full-size image from srcset
                    var imgSrc = "";
                    if (!string.IsNullOrEmpty(srcset))
                    {
                        imgSrc = srcset.Split(' ')[0];
                        // Remove the /v1 path part to get full image
                        var uri = new Uri(imgSrc);
                        var path = uri.AbsolutePath;
                        var v1Index = path.IndexOf("/v1", StringComparison.Ordinal);
                        if (v1Index >= 0)
                            imgSrc = uri.GetLeftPart(UriPartial.Authority) + path[..v1Index];
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = ariaLabel,
                        ImgSrc = string.IsNullOrEmpty(imgSrc) ? null : imgSrc,
                        Thumbnail = string.IsNullOrEmpty(thumbnailSrc) ? null : thumbnailSrc,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch { /* skip */ }
            }

            // Extract next page cursor URL
            var nextPageLink = document.QuerySelectorAll("a.vQ2brP").LastOrDefault();
            if (nextPageLink != null)
            {
                var nextUrl = nextPageLink.GetAttribute("href");
                if (!string.IsNullOrEmpty(nextUrl))
                {
                    _nextPageUrl = nextUrl.StartsWith("http") ? nextUrl : _baseUrl + nextUrl;
                    _nextPageUrl = _nextPageUrl.Replace("http://", "https://");
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
