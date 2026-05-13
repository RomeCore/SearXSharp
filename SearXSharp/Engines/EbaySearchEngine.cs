using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for eBay (ebay.com).
/// Uses HTML scraping (no API key required).
/// Based on SearXNG's ebay.py.
/// </summary>
public class EbaySearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.ebay.com";

    /// <inheritdoc />
    public override string Name => "ebay";

    /// <inheritdoc />
    public override string DisplayName => "eBay";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.Music };

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

    public EbaySearchEngine() : base() { }
    public EbaySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var url = $"{_baseUrl}/sch/i.html?_nkw={Uri.EscapeDataString(query.Query)}&_sacat={query.Page}";

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

            var items = document.QuerySelectorAll("li.s-item");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a.s-item__link");
                    if (link == null) continue;

                    var url = link.GetAttribute("href") ?? "";

                    var titleEl = item.QuerySelector("h3.s-item__title");
                    var title = titleEl?.TextContent.Trim() ?? "";
                    if (string.IsNullOrEmpty(title)) continue;

                    var contentEl = item.QuerySelector("div[span='SECONDARY_INFO']");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    var priceEl = item.QuerySelector("span.s-item__price");
                    var price = priceEl?.TextContent.Trim() ?? "";

                    var shippingEl = item.QuerySelector("span.s-item__shipping");
                    var shipping = shippingEl?.TextContent.Trim() ?? "";

                    var sourceEl = item.QuerySelector("span.s-item__location");
                    var sourceCountry = sourceEl?.TextContent.Trim() ?? "";

                    var img = item.QuerySelector("img.s-item__image-img");
                    var thumbnail = img?.GetAttribute("src") ?? "";

                    var metadata = price;
                    if (!string.IsNullOrEmpty(shipping))
                        metadata += $" | {shipping}";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                        Metadata = metadata,
                        Source = sourceCountry,
                        Engine = Name,
                        Category = SearchCategory.Files,
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
