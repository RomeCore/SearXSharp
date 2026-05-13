using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Qwant (www.qwant.com).
/// Uses Qwant's undocumented API (api.qwant.com/v3).
/// Based on SearXNG's qwant.py.
/// </summary>
public class QwantSearchEngine : SearchEngineBase
{
    private const string _apiUrl = "https://api.qwant.com/v3/search/";
    private const string _webLiteUrl = "https://lite.qwant.com/";

    /// <inheritdoc />
    public override string Name => "qwant";

    /// <inheritdoc />
    public override string DisplayName => "Qwant";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.News, SearchCategory.Images, SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 5;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    /// <summary>
    /// The Qwant category to search: "web-lite", "web", "news", "images", "videos".
    /// </summary>
    public string QwantCategory { get; set; } = "web-lite";

    public QwantSearchEngine() : base() { }
    public QwantSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            return QwantCategory switch
            {
                "web-lite" => await SearchWebLiteAsync(query, ct),
                "web" => await SearchApiAsync(query, "web", ct),
                "news" => await SearchApiAsync(query, "news", ct),
                "images" => await SearchApiAsync(query, "images", ct),
                "videos" => await SearchApiAsync(query, "videos", ct),
                _ => await SearchWebLiteAsync(query, ct),
            };
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResult("timeout", suspended: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private async Task<SearchResultList> SearchWebLiteAsync(SearchQuery query, CancellationToken ct)
    {
        var args = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["locale"] = "en_US",
            ["l"] = "en",
            ["s"] = ((int)query.SafeSearch).ToString(),
            ["p"] = query.Page.ToString(),
        };

        var url = _webLiteUrl + "?" + string.Join("&", args.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        using var request = CreateGetRequest(url);
        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        return ParseWebLiteResults(html);
    }

    private SearchResultList ParseWebLiteResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).Result;

            var items = document.QuerySelectorAll("section article");
            foreach (var item in items)
            {
                try
                {
                    // Skip ads (has tooltip span)
                    if (item.QuerySelector("span.tooltip") != null)
                        continue;

                    var urlEl = item.QuerySelector("span.url.partner");
                    var titleEl = item.QuerySelector("h2 a");
                    var contentEl = item.QuerySelector("p");

                    if (titleEl == null) continue;

                    var position = results.Count + 1;
                    results.Add(new SearchResult
                    {
                        Url = urlEl?.TextContent.Trim() ?? "",
                        Title = titleEl.TextContent.Trim(),
                        Content = contentEl?.TextContent.Trim() ?? "",
                        Engine = Name,
                        Category = SearchCategory.Web,
                        Position = position,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse web-lite results", Name);
        }

        return CreateResultList(results);
    }

    private async Task<SearchResultList> SearchApiAsync(SearchQuery query, string category, CancellationToken ct)
    {
        var args = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["count"] = (category == "images" ? "50" : "10"),
            ["locale"] = "en_US",
            ["safesearch"] = ((int)query.SafeSearch).ToString(),
            ["offset"] = ((query.Page - 1) * (category == "images" ? 50 : 10)).ToString(),
        };

        var url = _apiUrl + category + "?" + string.Join("&", args.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        using var request = CreateGetRequest(url);
        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseApiResults(json, category);
    }

    private SearchResultList ParseApiResults(string json, string category)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data))
                return CreateResultList(results);

            // Get items based on category
            List<JsonElement> items;

            if (category == "web")
            {
                var mainline = data.GetProperty("result").GetProperty("items").GetProperty("mainline");
                items = new List<JsonElement>();
                foreach (var row in mainline.EnumerateArray())
                {
                    if (row.GetProperty("type").GetString() == "web" && row.TryGetProperty("items", out var rowItems))
                    {
                        foreach (var item in rowItems.EnumerateArray())
                            items.Add(item);
                    }
                }
            }
            else
            {
                if (!data.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("items", out var resultItems))
                    return CreateResultList(results);
                items = resultItems.EnumerateArray().ToList();
            }

            foreach (var item in items)
            {
                try
                {
                    var title = item.GetProperty("title").GetString() ?? "";
                    var url = item.GetProperty("url").GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                        continue;

                    var result = category switch
                    {
                        "news" => ParseNewsItem(item, title, url),
                        "images" => ParseImageItem(item, title, url),
                        "videos" => ParseVideoItem(item, title, url),
                        _ => ParseWebItem(item, title, url),
                    };

                    if (result != null)
                        results.Add(result);
                }
                catch { /* skip */ }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse API JSON", Name);
        }

        return CreateResultList(results);
    }

    private static SearchResult? ParseWebItem(JsonElement item, string title, string url)
    {
        var content = "";
        if (item.TryGetProperty("desc", out var desc))
            content = desc.GetString() ?? "";

        return new SearchResult
        {
            Url = url,
            Title = title,
            Content = content,
            Engine = "qwant",
            Category = SearchCategory.Web,
        };
    }

    private static SearchResult? ParseNewsItem(JsonElement item, string title, string url)
    {
        DateTime? publishedDate = null;
        if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Number)
            publishedDate = DateTimeOffset.FromUnixTimeSeconds(dateEl.GetInt64()).DateTime;

        string? thumbnail = null;
        if (item.TryGetProperty("media", out var media) && media.EnumerateArray().Any())
        {
            var firstMedia = media.EnumerateArray().First();
            if (firstMedia.TryGetProperty("pict", out var pict) && pict.TryGetProperty("url", out var pictUrl))
                thumbnail = pictUrl.GetString();
        }

        return new SearchResult
        {
            Url = url,
            Title = title,
            Content = "",
            PublishedDate = publishedDate,
            Thumbnail = thumbnail,
            Engine = "qwant",
            Category = SearchCategory.News,
            Type = SearchResultType.News,
        };
    }

    private static SearchResult? ParseImageItem(JsonElement item, string title, string url)
    {
        string? thumbnail = null;
        if (item.TryGetProperty("thumbnail", out var thumbEl))
            thumbnail = thumbEl.GetString();

        string? imgSrc = null;
        if (item.TryGetProperty("media", out var mediaEl))
            imgSrc = mediaEl.GetString();

        var resolution = "";
        if (item.TryGetProperty("width", out var w) && item.TryGetProperty("height", out var h))
            resolution = $"{w.GetInt32()} x {h.GetInt32()}";

        return new SearchResult
        {
            Url = url,
            Title = title,
            Thumbnail = thumbnail,
            ImgSrc = imgSrc,
            Resolution = resolution,
            Engine = "qwant",
            Category = SearchCategory.Images,
            Type = SearchResultType.Image,
        };
    }

    private static SearchResult? ParseVideoItem(JsonElement item, string title, string url)
    {
        var content = "";
        if (item.TryGetProperty("desc", out var desc))
            content = desc.GetString() ?? "";

        TimeSpan? duration = null;
        if (item.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            duration = TimeSpan.FromMilliseconds(durEl.GetDouble());

        DateTime? publishedDate = null;
        if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Number)
            publishedDate = DateTimeOffset.FromUnixTimeSeconds(dateEl.GetInt64()).DateTime;

        string? thumbnail = null;
        if (item.TryGetProperty("thumbnail", out var thumbEl))
        {
            thumbnail = thumbEl.GetString()?.Replace("https://s2.qwant.com", "https://s1.qwant.com");
        }

        return new SearchResult
        {
            Url = url,
            Title = title,
            Content = content,
            Duration = duration,
            PublishedDate = publishedDate,
            Thumbnail = thumbnail,
            Engine = "qwant",
            Category = SearchCategory.Videos,
            Type = SearchResultType.Video,
        };
    }
}
