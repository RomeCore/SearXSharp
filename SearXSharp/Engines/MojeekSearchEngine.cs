using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Mojeek (www.mojeek.com).
/// Uses HTML scraping. Supports general web, images, and news search.
/// Based on SearXNG's mojeek.py.
/// </summary>
public class MojeekSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.mojeek.com";

    /// <inheritdoc />
    public override string Name => "mojeek";

    /// <inheritdoc />
    public override string DisplayName => "Mojeek";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.Images, SearchCategory.News };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    /// <summary>
    /// Search type: "" (general), "images", or "news".
    /// </summary>
    public string SearchType { get; set; } = "";

    public MojeekSearchEngine() : base() { }
    public MojeekSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["safe"] = Math.Min((int)query.SafeSearch, 1).ToString(),
            };

            if (!string.IsNullOrEmpty(SearchType))
                args["fmt"] = SearchType;

            // Paging (skip on first page to avoid rate limit)
            if (SearchType == "" && query.Page > 1)
                args["s"] = (10 * (query.Page - 1)).ToString();

            // Time range
            if (query.TimeRange.HasValue && SearchType != "images")
            {
                var days = query.TimeRange.Value switch
                {
                    TimeRange.Day => 1,
                    TimeRange.Week => 7,
                    TimeRange.Month => 30,
                    TimeRange.Year => 365,
                    _ => 0,
                };
                if (days > 0)
                    args["since"] = DateTime.UtcNow.AddDays(-days).ToString("yyyyMMdd");
            }

            var url = _baseUrl + "/search?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return SearchType switch
            {
                "images" => ParseImageResults(html),
                "news" => ParseNewsResults(html),
                _ => ParseGeneralResults(html),
            };
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

    private SearchResultList ParseGeneralResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).Result;

            var items = document.QuerySelectorAll("ul.results-standard > li > a.ob");
            foreach (var link in items)
            {
                try
                {
                    var url = link.GetAttribute("href") ?? "";
                    var titleEl = link.ParentElement?.QuerySelector("h2 a");
                    var contentEl = link.ParentElement?.QuerySelector("p.s");

                    var title = titleEl?.TextContent.Trim() ?? "";
                    var content = contentEl?.TextContent.Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                        continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = Name,
                        Category = SearchCategory.Web,
                    });
                }
                catch { /* skip */ }
            }

            // Suggestions
            var suggestions = document.QuerySelectorAll("div.top-info p.top-info.spell em a");
            foreach (var sug in suggestions)
            {
                // suggestions are handled implicitly
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse general results", Name);
        }

        return CreateResultList(results);
    }

    private SearchResultList ParseImageResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).Result;

            var items = document.QuerySelectorAll("div#results div[class*='image']");
            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a");
                    var url = link?.GetAttribute("href") ?? "";
                    var title = link?.GetAttribute("data-title") ?? "";
                    var imgSrc = link?.QuerySelector("img")?.GetAttribute("src") ?? "";

                    if (string.IsNullOrWhiteSpace(url)) continue;

                    if (!string.IsNullOrEmpty(imgSrc) && !imgSrc.StartsWith("http"))
                        imgSrc = _baseUrl + imgSrc;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        ImgSrc = imgSrc,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse image results", Name);
        }

        return CreateResultList(results);
    }

    private SearchResultList ParseNewsResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).Result;

            var items = document.QuerySelectorAll("section.news-search-result article");
            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("h2 a");
                    var url = link?.GetAttribute("href") ?? "";
                    var title = link?.TextContent.Trim() ?? "";
                    var contentEl = item.QuerySelector("p.s");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                        continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = Name,
                        Category = SearchCategory.News,
                        Type = SearchResultType.News,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse news results", Name);
        }

        return CreateResultList(results);
    }
}
