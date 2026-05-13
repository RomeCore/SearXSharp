using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Bing Images.
/// Uses Bing's async image endpoint with JSON metadata extraction.
/// Based on SearXNG's bing_images.py.
/// </summary>
public class BingImagesSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.bing.com/images/async";

    /// <inheritdoc />
    public override string Name => "bing images";

    /// <inheritdoc />
    public override string DisplayName => "Bing Images";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images, SearchCategory.Web };

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

    private static readonly Dictionary<TimeRange, int> _timeMap = new()
    {
        [TimeRange.Day] = 60 * 24,
        [TimeRange.Week] = 60 * 24 * 7,
        [TimeRange.Month] = 60 * 24 * 31,
        [TimeRange.Year] = 60 * 24 * 365,
    };

    public BingImagesSearchEngine() : base() { }
    public BingImagesSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["async"] = "1",
                ["first"] = ((query.Page - 1) * 35 + 1).ToString(),
                ["count"] = "35",
            };

            if (query.TimeRange.HasValue && _timeMap.TryGetValue(query.TimeRange.Value, out var minutes))
                queryParams["qft"] = "filterui:age-lt" + minutes;

            var url = _baseUrl + "?" + string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
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
        var context = BrowsingContext.New(Configuration.Default);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var items = doc.QuerySelectorAll("ul.dgControl_list > li");
        foreach (var item in items)
        {
            try
            {
                var iuscLink = item.QuerySelector("a.iusc");
                if (iuscLink == null) continue;

                var metadataAttr = iuscLink.GetAttribute("m");
                if (string.IsNullOrEmpty(metadataAttr)) continue;

                var metadata = JsonSerializer.Deserialize<BingImageMetadata>(metadataAttr);
                if (metadata == null) continue;

                var title = string.Join(" ", item.QuerySelectorAll("div.infnmpt a")
                    .Select(el => el.TextContent)).Trim();

                var imgFormatParts = item.QuerySelector("div.imgpt div span")
                    ?.TextContent?.Trim().Split(" · ");

                var source = string.Join(" ", item.QuerySelectorAll("div.imgpt div.lnkw a")
                    .Select(el => el.TextContent)).Trim();

                results.Add(new SearchResult
                {
                    Url = metadata.Purl ?? "",
                    Title = title,
                    Content = metadata.Desc ?? "",
                    Source = source,
                    Resolution = imgFormatParts?.Length > 0 ? imgFormatParts[0] : null,
                    ImgSrc = metadata.Murl,
                    Thumbnail = metadata.Turl,
                    Engine = Name,
                    Category = SearchCategory.Images,
                    Type = SearchResultType.Image,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "{Engine}: Failed to parse image result", Name);
            }
        }

        return CreateResultList(results);
    }

    private class BingImageMetadata
    {
        public string? Purl { get; set; }
        public string? Murl { get; set; }
        public string? Turl { get; set; }
        public string? Desc { get; set; }
    }
}
