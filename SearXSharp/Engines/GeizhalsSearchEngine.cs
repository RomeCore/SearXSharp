using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Geizhals (geizhals.de) - German price comparison website.
/// Compares prices across German shopping sites.
/// Based on SearXNG's geizhals.py.
/// </summary>
public partial class GeizhalsSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://geizhals.de";

    /// <inheritdoc />
    public override string Name => "geizhals";

    /// <inheritdoc />
    public override string DisplayName => "Geizhals";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    private static readonly Dictionary<string, string> _sortOrderMap = new()
    {
        ["relevance"] = null!,
        ["price"] = "p",
        ["asc"] = "p",
        ["desc"] = "-p",
    };

    public GeizhalsSearchEngine() : base() { }
    public GeizhalsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var searchQuery = query.Query;
            string? sort = null;

            // Extract sort order from query (sort:price, sort:asc, sort:desc)
            var sortMatch = SortRegex().Match(searchQuery);
            if (sortMatch.Success)
            {
                var sortKey = sortMatch.Groups[1].Value.ToLower();
                if (_sortOrderMap.TryGetValue(sortKey, out var sortVal))
                    sort = sortVal;
                searchQuery = SortRegex().Replace(searchQuery, "").Trim();
            }

            var args = new Dictionary<string, string>
            {
                ["fs"] = searchQuery,
                ["pg"] = query.Page.ToString(),
                ["toggle_all"] = "1",
            };
            if (sort != null)
                args["sort"] = sort;

            var url = _baseUrl + "/?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} product results", Name, results.Count);
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

            var items = document.QuerySelectorAll("article.listview__item");

            foreach (var item in items)
            {
                try
                {
                    var link = item.QuerySelector("a.listview__name-link");
                    var href = link?.GetAttribute("href") ?? "";
                    var url = href.StartsWith("http") ? href : _baseUrl + "/" + href;

                    var nameEl = item.QuerySelector("h3.listview__name");
                    var title = nameEl?.TextContent.Trim() ?? "";

                    var imgEl = item.QuerySelector("img.listview__image");
                    var thumbnail = imgEl?.GetAttribute("src");

                    // Specs
                    var specs = new List<string>();
                    var specItems = item.QuerySelectorAll("div.specs-grid__item");
                    foreach (var spec in specItems)
                    {
                        var dt = spec.QuerySelector("dt")?.TextContent.Trim() ?? "";
                        var dd = spec.QuerySelector("dd")?.TextContent.Trim() ?? "";
                        if (!string.IsNullOrEmpty(dt) && !string.IsNullOrEmpty(dd))
                            specs.Add($"{dt}: {dd}");
                    }
                    var content = string.Join(" | ", specs);

                    // Rating & offer count
                    var ratingEl = item.QuerySelector("div.stars-rating-label");
                    var offerCountEl = item.QuerySelector("div.listview__offercount");
                    var metadata = string.Join(", ", new[]
                    {
                        ratingEl?.TextContent.Trim(),
                        offerCountEl?.TextContent.Trim()
                    }.Where(m => !string.IsNullOrEmpty(m)));

                    // Price
                    var priceEl = item.QuerySelector("a.listview__price-link");
                    var priceText = priceEl?.TextContent.Trim() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Thumbnail = thumbnail,
                        Metadata = metadata,
                        Source = priceText,
                        Engine = Name,
                        Category = SearchCategory.General,
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

    [GeneratedRegex(@"sort:(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex SortRegex();
}
