using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for BitChute (bitchute.com).
/// Uses the BitChute beta search API (POST JSON, no API key required).
/// Based on SearXNG's bitchute.py.
/// </summary>
public class BitchuteSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.bitchute.com/api/beta/search/videos";
    private const string _baseVideoUrl = "https://www.bitchute.com/video/";
    private const string _embedUrl = "https://www.bitchute.com/embed/";

    /// <inheritdoc />
    public override string Name => "bitchute";

    /// <inheritdoc />
    public override string DisplayName => "BitChute";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public BitchuteSearchEngine() : base() { }
    public BitchuteSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var startIndex = (query.Page - 1) * 20;
            var requestBody = JsonSerializer.Serialize(new
            {
                offset = startIndex,
                limit = 20,
                query = query.Query,
                sensitivity_id = "normal",
                sort = "new",
            });

            using var request = CreateJsonPostRequest(_searchUrl, requestBody);
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

            if (!root.TryGetProperty("videos", out var videos))
                return CreateResultList(results);

            foreach (var item in videos.EnumerateArray())
            {
                try
                {
                    var videoName = item.GetProperty("video_name").GetString() ?? "";
                    var videoId = item.GetProperty("video_id").GetString() ?? "";
                    var description = item.GetProperty("description").GetString() ?? "";

                    var channel = item.GetProperty("channel");
                    var channelName = channel.GetProperty("channel_name").GetString() ?? "";

                    var dateStr = item.GetProperty("date_published").GetString() ?? "";
                    var duration = item.GetProperty("duration").GetString() ?? "";
                    var viewCount = item.GetProperty("view_count").GetString() ?? "";
                    var thumbnailUrl = item.GetProperty("thumbnail_url").GetString() ?? "";

                    DateTime? publishedDate = null;
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                        publishedDate = dt;

                    var url = _baseVideoUrl + videoId;
                    var iframeSrc = _embedUrl + videoId;
                    long views = long.TryParse(viewCount, out var v) ? v : 0;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = videoName,
                        Content = description,
                        Author = channelName,
                        PublishedDate = publishedDate,
                        Thumbnail = !string.IsNullOrEmpty(thumbnailUrl) ? thumbnailUrl : null,
                        IframeSrc = iframeSrc,
                        Views = views,
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
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
