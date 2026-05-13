using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Pixiv (Japanese illustration platform).
/// Uses Pixiv's internal AJAX API (scraping).
/// Based on SearXNG's pixiv.py.
/// </summary>
public class PixivSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.pixiv.net/ajax/search/illustrations";
    private const string _imageProxy = "https://i.pximg.net";

    /// <inheritdoc />
    public override string Name => "pixiv";

    /// <inheritdoc />
    public override string DisplayName => "Pixiv";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 50;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public PixivSearchEngine() : base() { }
    public PixivSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["word"] = query.Query,
                ["order"] = "date_d",
                ["mode"] = "all",
                ["p"] = query.Page.ToString(),
                ["s_mode"] = "s_tag_full",
                ["type"] = "illust_and_ugoira",
                ["lang"] = "en",
            };

            var url = $"{_baseUrl}/{Uri.EscapeDataString(query.Query)}?"
                + string.Join("&", queryParams.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
            // Pixiv needs a proper Referer header
            request.Headers.TryAddWithoutValidation("Referer", "https://www.pixiv.net/");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

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

            if (!root.TryGetProperty("body", out var body)
                || !body.TryGetProperty("illust", out var illust)
                || !illust.TryGetProperty("data", out var data))
                return CreateResultList(results);

            foreach (var item in data.EnumerateArray())
            {
                try
                {
                    var title = item.GetProperty("title").GetString() ?? "";
                    var imageUrl = item.GetProperty("url").GetString() ?? "";

                    var alt = "";
                    if (item.TryGetProperty("alt", out var altEl))
                        alt = altEl.GetString() ?? "";

                    var userName = "";
                    if (item.TryGetProperty("userName", out var user))
                        userName = user.GetString() ?? "";

                    var userId = 0;
                    if (item.TryGetProperty("userId", out var uid))
                        userId = uid.GetInt32();

                    // Build proxy URLs (Pixiv blocks direct hotlinking)
                    var thumbnail = imageUrl; // Use as-is, let the client handle proxy
                    var fullImageUrl = imageUrl
                        .Replace("/c/250x250_80_a2/", "/")
                        .Replace("_square1200.jpg", "_master1200.jpg")
                        .Replace("custom-thumb", "img-master")
                        .Replace("_custom1200.jpg", "_master1200.jpg");

                    results.Add(new SearchResult
                    {
                        Url = fullImageUrl,
                        Title = title,
                        Content = alt,
                        ImgSrc = fullImageUrl,
                        Thumbnail = thumbnail,
                        Author = $"{userName} (ID: {userId})",
                        Source = "pixiv.net",
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse illustration", Name);
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
