using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Dailymotion (www.dailymotion.com).
/// Uses official Dailymotion API (no API key required).
/// Based on SearXNG's dailymotion.py.
/// </summary>
public class DailymotionSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.dailymotion.com/videos?";
    private const string _iframeSrc = "https://www.dailymotion.com/embed/video/{0}";

    private static readonly Dictionary<TimeRange, int> _timeRangeMap = new()
    {
        [TimeRange.Day] = 86400,
        [TimeRange.Week] = 604800,
        [TimeRange.Month] = 2678400,
        [TimeRange.Year] = 31536000,
    };

    private static readonly Dictionary<SafeSearchLevel, string> _familyFilterMap = new()
    {
        [SafeSearchLevel.None] = "false",
        [SafeSearchLevel.Moderate] = "true",
        [SafeSearchLevel.Strict] = "true",
    };

    private static readonly Dictionary<SafeSearchLevel, string> _kidsFilterMap = new()
    {
        [SafeSearchLevel.None] = "",
        [SafeSearchLevel.Moderate] = "true",
        [SafeSearchLevel.Strict] = "true",
    };

    /// <inheritdoc />
    public override string Name => "dailymotion";

    /// <inheritdoc />
    public override string DisplayName => "Dailymotion";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

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

    public DailymotionSearchEngine() : base() { }
    public DailymotionSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["search"] = query.Query,
                ["family_filter"] = _familyFilterMap.GetValueOrDefault(query.SafeSearch, "false"),
                ["thumbnail_ratio"] = "original",
                ["page"] = query.Page.ToString(),
                ["password_protected"] = "false",
                ["private"] = "false",
                ["sort"] = "relevance",
                ["limit"] = "10",
                ["fields"] = "allow_embed,description,title,created_time,duration,url,thumbnail_360_url,id",
            };

            // Kids filter
            var kidsVal = _kidsFilterMap.GetValueOrDefault(query.SafeSearch, "");
            if (!string.IsNullOrEmpty(kidsVal))
                args["is_created_for_kids"] = kidsVal;

            // Time range
            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var seconds))
            {
                var createdAfter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
                args["created_after"] = createdAfter.ToString();
            }

            var url = _searchUrl + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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

            if (root.TryGetProperty("error", out var err))
            {
                _logger.Error("{Engine}: API error: {Error}", Name, err.GetProperty("message").GetString());
                return CreateResultList(results);
            }

            if (!root.TryGetProperty("list", out var list))
                return CreateResultList(results);

            foreach (var item in list.EnumerateArray())
            {
                try
                {
                    var title = item.GetProperty("title").GetString() ?? "";
                    var url = item.GetProperty("url").GetString() ?? "";
                    var videoId = item.GetProperty("id").GetString() ?? "";

                    var description = "";
                    if (item.TryGetProperty("description", out var desc))
                        description = StripHtml(desc.GetString() ?? "");
                    if (description.Length > 300)
                        description = description[..300] + "...";

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("created_time", out var createdEl))
                    {
                        var unixTime = createdEl.GetInt64();
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }

                    var duration = TimeSpan.Zero;
                    if (item.TryGetProperty("duration", out var durEl))
                        duration = TimeSpan.FromSeconds(durEl.GetInt32());

                    var thumbnail = "";
                    if (item.TryGetProperty("thumbnail_360_url", out var thumbEl))
                    {
                        thumbnail = (thumbEl.GetString() ?? "").Replace("http://", "https://");
                    }

                    var allowEmbed = false;
                    if (item.TryGetProperty("allow_embed", out var embedEl))
                        allowEmbed = embedEl.GetBoolean();

                    string? iframesrc = allowEmbed ? string.Format(_iframeSrc, videoId) : null;

                    var result = new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        PublishedDate = publishedDate,
                        Duration = duration,
                        Thumbnail = thumbnail,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                        IframeSrc = iframesrc
                    };

                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse video item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ").Trim();
    }
}
