using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for F-Droid (f-droid.org).
/// F-Droid is a repository of FOSS (Free and Open Source) applications for Android.
/// Based on SearXNG's fdroid.py.
/// </summary>
public class FDroidSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://search.f-droid.org";
    private const string _searchUrl = "https://search.f-droid.org/";

    /// <inheritdoc />
    public override string Name => "fdroid";

    /// <inheritdoc />
    public override string DisplayName => "F-Droid";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.IT, SearchCategory.Packages };

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

    public FDroidSearchEngine() : base() { }
    public FDroidSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _searchUrl + "?q=" + Uri.EscapeDataString(query.Query)
                + "&page=" + query.Page
                + "&lang=";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} F-Droid results", Name, results.Count);
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

            // Each app is in an <a class="package-header">
            var items = document.QuerySelectorAll("a.package-header");

            foreach (var item in items)
            {
                try
                {
                    var href = item.GetAttribute("href");
                    if (string.IsNullOrEmpty(href)) continue;

                    var url = href.StartsWith("http") ? href : _baseUrl + href;

                    var titleElement = item.QuerySelector("h4.package-name");
                    var title = titleElement?.TextContent.Trim() ?? string.Empty;

                    var summaryElement = item.QuerySelector("span.package-summary");
                    var summary = summaryElement?.TextContent.Trim() ?? string.Empty;

                    var licenseElement = item.QuerySelector("span.package-license");
                    var license = licenseElement?.TextContent.Trim() ?? string.Empty;

                    var content = summary;
                    if (!string.IsNullOrEmpty(license))
                        content += " - " + license;

                    var imgElement = item.QuerySelector("img.package-icon");
                    var thumbnail = imgElement?.GetAttribute("src");
                    if (!string.IsNullOrEmpty(thumbnail) && !thumbnail.StartsWith("http"))
                        thumbnail = _baseUrl + thumbnail;

                    if (string.IsNullOrEmpty(title)) continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Thumbnail = thumbnail,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse F-Droid app", Name);
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
