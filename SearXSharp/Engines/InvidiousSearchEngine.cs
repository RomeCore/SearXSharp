using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Invidious (invidious API).
/// Uses the public Invidious API (no key required) from configurable instances.
/// Based on SearXNG's invidious.py.
/// </summary>
public class InvidiousSearchEngine : SearchEngineBase
{
    // Default public instance (can be changed via configuration)
    private const string _defaultBaseUrl = "https://inv.nadeko.net";

    private static readonly Dictionary<TimeRange, string> _timeRangeDict = new()
    {
        [TimeRange.Day] = "today",
        [TimeRange.Week] = "week",
        [TimeRange.Month] = "month",
        [TimeRange.Year] = "year",
    };

    /// <summary>
    /// Base URL of the Invidious instance to use.
    /// </summary>
    public string BaseUrl { get; set; } = _defaultBaseUrl;

    /// <inheritdoc />
    public override string Name => "invidious";

    /// <inheritdoc />
    public override string DisplayName => "Invidious";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos, SearchCategory.Music };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    public InvidiousSearchEngine() : base() { }
    public InvidiousSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var searchUrl = $"{BaseUrl.TrimEnd('/')}/api/v1/search?" +
                $"q={Uri.EscapeDataString(query.Query)}&page={query.Page}";

            if (query.TimeRange.HasValue && _timeRangeDict.TryGetValue(query.TimeRange.Value, out var dateRange))
                searchUrl += $"&date={dateRange}";

            using var request = CreateGetRequest(searchUrl);
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

            foreach (var result in root.EnumerateArray())
            {
                try
                {
                    var rtype = result.TryGetProperty("type", out var typeEl)
                        ? typeEl.GetString() ?? "" : "";

                    if (rtype != "video") continue;

                    var videoId = result.TryGetProperty("videoId", out var vidEl)
                        ? vidEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(videoId)) continue;

                    var baseUrl = BaseUrl.TrimEnd('/');
                    var url = $"{baseUrl}/watch?v={videoId}";
                    var iframeSrc = $"{baseUrl}/embed/{videoId}";

                    var title = result.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var description = result.TryGetProperty("description", out var d)
                        ? d.GetString() ?? "" : "";
                    var author = result.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";

                    var thumbnail = "";
                    if (result.TryGetProperty("videoThumbnails", out var thumbs))
                    {
                        foreach (var thumb in thumbs.EnumerateArray())
                        {
                            if (thumb.TryGetProperty("quality", out var q) && q.GetString() == "sddefault")
                            {
                                thumbnail = thumb.GetProperty("url").GetString() ?? "";
                                // Fix partial URLs
                                if (!string.IsNullOrEmpty(thumbnail) && !thumbnail.Contains("://"))
                                    thumbnail = baseUrl + thumbnail;
                                break;
                            }
                        }
                    }

                    DateTime? publishedDate = null;
                    if (result.TryGetProperty("published", out var pubEl))
                    {
                        var unixTime = pubEl.GetInt64();
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }

                    TimeSpan? duration = null;
                    if (result.TryGetProperty("lengthSeconds", out var lenEl))
                    {
                        duration = TimeSpan.FromSeconds(lenEl.GetInt32());
                    }

                    long views = 0;
                    if (result.TryGetProperty("viewCount", out var viewsEl))
                        views = viewsEl.GetInt64();

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Author = author,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        IframeSrc = iframeSrc,
                        Duration = duration,
                        PublishedDate = publishedDate,
                        Views = views,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse video result", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
