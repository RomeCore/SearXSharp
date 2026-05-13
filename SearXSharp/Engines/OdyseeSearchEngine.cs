using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Odysee (odysee.com).
/// Uses the lighthouse.odysee.tv search API (unofficial, no API key required).
/// Based on SearXNG's odysee.py.
/// </summary>
public class OdyseeSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://lighthouse.odysee.tv/search";
    private const string _odyseeUrl = "https://odysee.com";
    private const string _embedUrl = "https://odysee.com/$/embed";
    private const string _thumbBaseUrl = "https://thumbnails.odycdn.com/optimize/s:390:0/quality:85/plain/";

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "today",
        [TimeRange.Week] = "thisweek",
        [TimeRange.Month] = "thismonth",
        [TimeRange.Year] = "thisyear",
    };

    /// <inheritdoc />
    public override string Name => "odysee";

    /// <inheritdoc />
    public override string DisplayName => "Odysee";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public OdyseeSearchEngine() : base() { }
    public OdyseeSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var startIndex = (query.Page - 1) * 20;
            var queryParams = new Dictionary<string, string>
            {
                ["s"] = query.Query,
                ["size"] = "20",
                ["from"] = startIndex.ToString(),
                ["include"] = "channel,thumbnail_url,title,description,duration,release_time",
                ["mediaType"] = "video",
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var timeFilter))
                queryParams["time_filter"] = timeFilter;

            var url = _baseUrl + "?" + string.Join("&",
                queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(json);
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

    private SearchResultList ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var item in root.EnumerateArray())
            {
                try
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    var claimId = item.GetProperty("claimId").GetString() ?? "";
                    var title = item.GetProperty("title").GetString() ?? "";
                    var thumbnailUrl = item.GetProperty("thumbnail_url").GetString() ?? "";
                    var description = item.GetProperty("description").GetString() ?? "";
                    var channel = item.GetProperty("channel").GetString() ?? "";
                    var releaseTime = item.GetProperty("release_time").GetString() ?? "";
                    var duration = item.GetProperty("duration").GetInt64();

                    DateTime? publishedDate = null;
                    if (!string.IsNullOrEmpty(releaseTime))
                    {
                        var datePart = releaseTime.Split('T')[0];
                        if (DateTime.TryParse(datePart, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    var url = $"{_odyseeUrl}/{name}:{claimId}";
                    var iframeUrl = $"{_embedUrl}/{name}:{claimId}";
                    var thumbnail = $"{_thumbBaseUrl}{thumbnailUrl}";
                    var formattedDuration = FormatDuration(duration);

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Author = channel,
                        PublishedDate = publishedDate,
                        Duration = formattedDuration,
                        Thumbnail = thumbnail,
                        IframeSrc = iframeUrl,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON response", Name);
        }

        return CreateResultList(results);
    }

    private static TimeSpan? FormatDuration(long seconds)
    {
        if (seconds <= 0) return null;
        return TimeSpan.FromSeconds(seconds);
    }
}
