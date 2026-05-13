using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Bing Videos (www.bing.com/videos).
/// Uses HTML scraping of Bing's async video search endpoint.
/// Based on SearXNG's bing_videos.py.
/// </summary>
public class BingVideosSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.bing.com/videos/asyncv2";

    // Time range mapping (minutes)
    private static readonly Dictionary<TimeRange, string> _timeMap = new()
    {
        [TimeRange.Day] = "1440",
        [TimeRange.Week] = "10080",
        [TimeRange.Month] = "43200",
        [TimeRange.Year] = "525600",
    };

    /// <inheritdoc />
    public override string Name => "bing_videos";

    /// <inheritdoc />
    public override string DisplayName => "Bing Videos";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos, SearchCategory.Web };

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

    public BingVideosSearchEngine() : base() { }
    public BingVideosSearchEngine(ILogger logger) : base(logger) { }

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
                ["async"] = "content",
                ["first"] = ((query.Page - 1) * 35 + 1).ToString(),
                ["count"] = "35",
            };

            if (query.TimeRange.HasValue && _timeMap.TryGetValue(query.TimeRange.Value, out var minutes))
            {
                queryParams["form"] = "VRFLTR";
                queryParams["qft"] = $" filterui:videoage-lt{minutes}";
            }

            var url = _baseUrl + "?" + string.Join("&",
                queryParams.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));

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
            var config = AngleSharp.Configuration.Default;
            var context = AngleSharp.BrowsingContext.New(config);
			var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

			var videoElements = document.QuerySelectorAll("div[id^='mc_vtvc_video']");

            foreach (var element in videoElements)
            {
                try
                {
                    var vrhmElement = element.QuerySelector("div.vrhdata");
                    var metadataJson = vrhmElement?.GetAttribute("vrhm");
                    if (string.IsNullOrEmpty(metadataJson)) continue;

                    using var metaDoc = JsonDocument.Parse(metadataJson);
                    var meta = metaDoc.RootElement;

                    var url = meta.GetProperty("murl").GetString() ?? "";
                    var title = meta.TryGetProperty("vt", out var vt) ? vt.GetString() ?? "" : "";
                    var duration = meta.TryGetProperty("du", out var du) ? du.GetString() ?? "" : "";

                    var metaBlock = element.QuerySelector("div.mc_vtvc_meta_block");
                    var info = metaBlock?.TextContent?.Trim() ?? "";

                    var img = element.QuerySelector("img[class^='rms']");
                    var thumbnail = img?.GetAttribute("data-src-hq") ?? "";

                    TimeSpan? parsedDuration = null;
                    if (!string.IsNullOrEmpty(duration))
                    {
                        if (TimeSpan.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var ts))
                            parsedDuration = ts;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = info,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        Duration = parsedDuration,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse video item", Name);
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
