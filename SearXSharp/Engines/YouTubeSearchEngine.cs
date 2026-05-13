using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for YouTube (no API - HTML scraping).
/// Extracts ytInitialData from page, handles pagination via continuation tokens.
/// Based on SearXNG's youtube_noapi.py.
/// </summary>
public class YouTubeSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://www.youtube.com/results";
    private const string _nextPageUrl = "https://www.youtube.com/youtubei/v1/search?key=AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
    private const string _baseVideoUrl = "https://www.youtube.com/watch?v=";
    private const string _embedUrl = "https://www.youtube-nocookie.com/embed/";

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "Ag",
        [TimeRange.Week] = "Aw",
        [TimeRange.Month] = "BA",
        [TimeRange.Year] = "BQ",
    };

    private static readonly Regex _ytInitialDataRegex = new(
        @"ytInitialData\s*=\s*({.*?});</script>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <inheritdoc />
    public override string Name => "youtube";

    /// <inheritdoc />
    public override string DisplayName => "YouTube";

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
    public override double Timeout => 10.0;

    // State for continuation token (in a real app this would be per-session)
    private string? _nextPageToken;

    public YouTubeSearchEngine() : base() { }
    public YouTubeSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            if (string.IsNullOrEmpty(_nextPageToken) || query.Page <= 1)
            {
                // First page - use HTML search
                var url = _searchUrl + "?search_query=" + Uri.EscapeDataString(query.Query)
                    + "&page=" + query.Page;

                if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tr))
                    url += "&sp=EgII" + Uri.EscapeDataString(tr) + "%253D%253D";

                var request = CreateGetRequest(url);
                request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+");

                var response = await SendRequestAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync(ct);
                return ParseFirstPage(html);
            }
            else
            {
                // Subsequent pages - use API with continuation token
                var payload = JsonSerializer.Serialize(new
                {
                    context = new
                    {
                        client = new
                        {
                            clientName = "WEB",
                            clientVersion = "2.20210310.12.01"
                        }
                    },
                    continuation = _nextPageToken
                });

                var request = CreateJsonPostRequest(_nextPageUrl, payload);
                request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+");

                var response = await SendRequestAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return ParseNextPage(json);
            }
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

    private SearchResultList ParseFirstPage(string html)
    {
        var results = new List<SearchResult>();

        var match = _ytInitialDataRegex.Match(html);
        if (!match.Success)
            return CreateResultList(results);

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;

            var contents = root
                .GetProperty("contents")
                .GetProperty("twoColumnSearchResultsRenderer")
                .GetProperty("primaryContents")
                .GetProperty("sectionListRenderer")
                .GetProperty("contents");

            foreach (var section in contents.EnumerateArray())
            {
                // Check for continuation token
                if (section.TryGetProperty("continuationItemRenderer", out var continuation))
                {
                    try
                    {
                        _nextPageToken = continuation
                            .GetProperty("continuationEndpoint")
                            .GetProperty("continuationCommand")
                            .GetProperty("token")
                            .GetString();
                    }
                    catch { }
                    continue;
                }

                if (!section.TryGetProperty("itemSectionRenderer", out var itemSection))
                    continue;

                if (!itemSection.TryGetProperty("contents", out var sectionContents))
                    continue;

                foreach (var container in sectionContents.EnumerateArray())
                {
                    if (!container.TryGetProperty("videoRenderer", out var video))
                        continue;

                    var result = ParseVideoRenderer(video);
                    if (result != null)
                        results.Add(result);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Engine}: Failed to parse first page", Name);
        }

        return CreateResultList(results);
    }

    private SearchResultList ParseNextPage(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var commands = root.GetProperty("onResponseReceivedCommands")[0]
                .GetProperty("appendContinuationItemsAction")
                .GetProperty("continuationItems");

            foreach (var section in commands.EnumerateArray())
            {
                if (!section.TryGetProperty("itemSectionRenderer", out var itemSection))
                {
                    // Check for next continuation token
                    if (section.TryGetProperty("continuationItemRenderer", out var continuation))
                    {
                        try
                        {
                            _nextPageToken = continuation
                                .GetProperty("continuationEndpoint")
                                .GetProperty("continuationCommand")
                                .GetProperty("token")
                                .GetString();
                        }
                        catch { }
                    }
                    continue;
                }

                if (!itemSection.TryGetProperty("contents", out var contents))
                    continue;

                foreach (var container in contents.EnumerateArray())
                {
                    if (!container.TryGetProperty("videoRenderer", out var video))
                        continue;

                    var result = ParseVideoRenderer(video);
                    if (result != null)
                        results.Add(result);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Engine}: Failed to parse next page", Name);
        }

        return CreateResultList(results);
    }

    private SearchResult? ParseVideoRenderer(JsonElement video)
    {
        try
        {
            var videoId = video.GetProperty("videoId").GetString();
            if (string.IsNullOrEmpty(videoId)) return null;

            var title = GetTextFromJson(video.GetProperty("title"));

            var content = "";
            if (video.TryGetProperty("descriptionSnippet", out var desc))
                content = GetTextFromJson(desc);

            var author = "";
            if (video.TryGetProperty("ownerText", out var owner))
                author = GetTextFromJson(owner);

            var length = "";
            if (video.TryGetProperty("lengthText", out var lengthText))
                length = GetTextFromJson(lengthText);

            var thumbnail = "";
            if (video.TryGetProperty("thumbnail", out var thumb)
                && thumb.TryGetProperty("thumbnails", out var thumbs))
            {
                var thumbnails = thumbs.EnumerateArray().ToList();
                if (thumbnails.Count > 0)
                    thumbnail = thumbnails[^1].GetProperty("url").GetString() ?? "";
            }

            if (string.IsNullOrEmpty(thumbnail))
                thumbnail = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            return new SearchResult
            {
                Url = _baseVideoUrl + videoId,
                Title = title,
                Content = content,
                Author = author,
                Duration = ParseDuration(length),
                Thumbnail = thumbnail,
                IframeSrc = _embedUrl + videoId,
                Engine = Name,
                Category = SearchCategory.Videos,
                Type = SearchResultType.Video,
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Engine}: Failed to parse video renderer", Name);
            return null;
        }
    }

    private static string GetTextFromJson(JsonElement element)
    {
        if (element.TryGetProperty("runs", out var runs))
        {
            var text = "";
            foreach (var run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("text", out var t))
                    text += t.GetString();
            }
            return text;
        }
        if (element.TryGetProperty("simpleText", out var simple))
            return simple.GetString() ?? "";
        return "";
    }

    private static TimeSpan? ParseDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return null;
        try
        {
            var parts = duration.Split(':');
            if (parts.Length == 2)
                return TimeSpan.FromMinutes(int.Parse(parts[0])) + TimeSpan.FromSeconds(int.Parse(parts[1]));
            if (parts.Length == 3)
                return TimeSpan.FromHours(int.Parse(parts[0]))
                    + TimeSpan.FromMinutes(int.Parse(parts[1]))
                    + TimeSpan.FromSeconds(int.Parse(parts[2]));
        }
        catch { }
        return null;
    }
}
