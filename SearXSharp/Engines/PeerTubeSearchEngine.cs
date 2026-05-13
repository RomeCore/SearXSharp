using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for PeerTube (peer.tube).
/// Uses the official PeerTube search API (no API key required).
/// Based on SearXNG's peertube.py and sepiasearch.py.
/// </summary>
public class PeerTubeSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://peer.tube";
    private const string _searchUrl = _baseUrl + "/api/v1/search/videos";

    private static readonly Dictionary<SafeSearchLevel, string> _safeSearchMap = new()
    {
        [SafeSearchLevel.None] = "both",
        [SafeSearchLevel.Moderate] = "false",
        [SafeSearchLevel.Strict] = "false",
    };

    /// <inheritdoc />
    public override string Name => "peertube";

    /// <inheritdoc />
    public override string DisplayName => "PeerTube";

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
    public override double Timeout => 12.0;

    public PeerTubeSearchEngine() : base() { }
    public PeerTubeSearchEngine(ILogger logger) : base(logger) { }

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
                ["search"] = query.Query,
                ["searchTarget"] = "search-index",
                ["resultType"] = "videos",
                ["start"] = ((query.Page - 1) * 10).ToString(),
                ["count"] = "10",
                ["sort"] = "-match",
                ["nsfw"] = _safeSearchMap.GetValueOrDefault(query.SafeSearch, "both"),
            };

            var url = _searchUrl + "?" + string.Join("&",
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

            if (!root.TryGetProperty("data", out var data))
                return CreateResultList(results);

            foreach (var item in data.EnumerateArray())
            {
                try
                {
                    var url = item.GetProperty("url").GetString() ?? "";
                    var title = item.GetProperty("name").GetString() ?? "";

                    var description = "";
                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        description = desc.GetString() ?? "";

                    var author = "";
                    if (item.TryGetProperty("account", out var account)
                        && account.TryGetProperty("displayName", out var displayName))
                        author = displayName.GetString() ?? "";

                    var channelName = "";
                    var channelHost = "";
                    if (item.TryGetProperty("channel", out var channel))
                    {
                        if (channel.TryGetProperty("displayName", out var chDisplay))
                            channelName = chDisplay.GetString() ?? "";
                        if (channel.TryGetProperty("host", out var host))
                            channelHost = host.GetString() ?? "";
                    }

                    var tags = new List<string>();
                    if (item.TryGetProperty("tags", out var tagsEl))
                        tags = tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").ToList();

                    var metadataParts = new List<string>();
                    if (!string.IsNullOrEmpty(channelName)) metadataParts.Add(channelName);
                    if (!string.IsNullOrEmpty(channelHost)) metadataParts.Add(channelHost);
                    if (tags.Count > 0) metadataParts.Add(string.Join(", ", tags));

                    var duration = item.GetProperty("duration").GetDouble();
                    TimeSpan? parsedDuration = duration > 0 ? TimeSpan.FromSeconds(duration) : null;

                    long views = 0;
                    if (item.TryGetProperty("views", out var viewsEl))
                        views = viewsEl.GetInt64();

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("publishedAt", out var pubEl))
                    {
                        if (DateTime.TryParse(pubEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    var iframeSrc = item.TryGetProperty("embedUrl", out var embed)
                        ? embed.GetString() ?? ""
                        : "";

                    var thumbnail = item.TryGetProperty("thumbnailUrl", out var thumb)
                        ? thumb.GetString() ?? ""
                        : item.TryGetProperty("previewUrl", out var preview)
                            ? preview.GetString() ?? ""
                            : "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Author = author,
                        Duration = parsedDuration,
                        Views = views,
                        PublishedDate = publishedDate,
                        IframeSrc = iframeSrc,
                        Thumbnail = thumbnail,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                        Metadata = string.Join(" | ", metadataParts),
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
