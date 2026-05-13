using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Bilibili (bilibili.com).
/// Uses the Bilibili internal search API.
/// Based on SearXNG's bilibili.py.
/// </summary>
public class BilibiliSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.bilibili.com/x/web-interface/search/type";
    private const string _baseUrl = "https://www.bilibili.com";
    private const string _embedUrl = "https://player.bilibili.com/player.html";

    /// <inheritdoc />
    public override string Name => "bilibili";

    /// <inheritdoc />
    public override string DisplayName => "Bilibili";

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

    public BilibiliSearchEngine() : base() { }
    public BilibiliSearchEngine(ILogger logger) : base(logger) { }

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
                ["__refresh__"] = "true",
                ["page"] = query.Page.ToString(),
                ["page_size"] = "20",
                ["single_column"] = "0",
                ["keyword"] = query.Query,
                ["search_type"] = "video",
            };

            var url = _searchUrl + "?" + string.Join("&",
                queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            // Bilibili requires a Referer header
            request.Headers.TryAddWithoutValidation("Referer", _baseUrl);

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

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("result", out var resultArr))
                return CreateResultList(results);

            foreach (var item in resultArr.EnumerateArray())
            {
                try
                {
                    var titleRaw = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var title = System.Net.WebUtility.HtmlDecode(titleRaw);

                    var url = item.GetProperty("arcurl").GetString() ?? "";
                    var thumbnail = item.GetProperty("pic").GetString() ?? "";
                    var description = item.TryGetProperty("description", out var d)
                        ? d.GetString() ?? "" : "";
                    var author = item.GetProperty("author").GetString() ?? "";
                    var aid = item.GetProperty("aid").GetInt64();

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("pubdate", out var pubEl))
                    {
                        var unixTime = pubEl.GetInt64();
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }

                    TimeSpan? duration = null;
                    if (item.TryGetProperty("duration", out var durStr))
                    {
                        var dur = durStr.GetString() ?? "";
                        var parts = dur.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var sec))
                        {
                            duration = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
                            // Invalid if more than 60 minutes
                            if (duration > TimeSpan.FromMinutes(60))
                                duration = null;
                        }
                    }

                    var iframeUrl = $"{_embedUrl}?aid={aid}&high_quality=1&autoplay=false";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Author = author,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        IframeSrc = iframeUrl,
                        Duration = duration,
                        PublishedDate = publishedDate,
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
