using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Genius (genius.com) — song lyrics and artist info.
/// Uses undocumented Genius API (no API key required).
/// Based on SearXNG's genius.py.
/// </summary>
public class GeniusSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://genius.com/api/search/multi?{0}&page={1}&per_page=5";
    private const string _musicPlayer = "https://genius.com{0}/apple_music_player";

    /// <inheritdoc />
    public override string Name => "genius";

    /// <inheritdoc />
    public override string DisplayName => "Genius";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Music };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public GeniusSearchEngine() : base() { }
    public GeniusSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var qs = string.Join("&", new Dictionary<string, string>
            {
                ["q"] = query.Query,
            }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var url = string.Format(_searchUrl, qs, query.Page);

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

            if (!root.TryGetProperty("response", out var response)
                || !response.TryGetProperty("sections", out var sections))
                return CreateResultList(results);

            foreach (var section in sections.EnumerateArray())
            {
                if (!section.TryGetProperty("hits", out var hits))
                    continue;

                foreach (var hit in hits.EnumerateArray())
                {
                    try
                    {
                        var type = hit.GetProperty("type").GetString() ?? "";
                        var result = hit.GetProperty("result");

                        switch (type)
                        {
                            case "song":
                            case "lyric":
                                results.Add(ParseLyric(result, hit));
                                break;
                            case "artist":
                                results.Add(ParseArtist(result));
                                break;
                            case "album":
                                results.Add(ParseAlbum(result));
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "{Engine}: Failed to parse hit", Name);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }

    private SearchResult ParseLyric(JsonElement result, JsonElement hit)
    {
        var url = result.GetProperty("url").GetString() ?? "";
        var fullTitle = result.GetProperty("full_title").GetString() ?? "";
        var thumbnail = "";
        if (result.TryGetProperty("song_art_image_thumbnail_url", out var thumb))
            thumbnail = thumb.GetString() ?? "";

        // Content from highlights
        var content = "";
        if (hit.TryGetProperty("highlights", out var highlights) && highlights.GetArrayLength() > 0)
            content = highlights[0].GetProperty("value").GetString() ?? "";
        if (string.IsNullOrEmpty(content))
            content = fullTitle;

        DateTime? publishedDate = null;
        if (result.TryGetProperty("lyrics_updated_at", out var ts) && ts.ValueKind == JsonValueKind.Number)
            publishedDate = DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64()).DateTime;

        var apiPath = "";
        if (result.TryGetProperty("api_path", out var ap))
            apiPath = ap.GetString() ?? "";

        var searchResult = new SearchResult
        {
            Url = url,
            Title = fullTitle,
            Content = content,
            Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
            PublishedDate = publishedDate,
            Engine = Name,
            Category = SearchCategory.Music,
            IframeSrc = !string.IsNullOrEmpty(apiPath) ? string.Format(_musicPlayer, apiPath) : null
        };

        return searchResult;
    }

    private static SearchResult ParseArtist(JsonElement result)
    {
        var url = result.GetProperty("url").GetString() ?? "";
        var name = result.GetProperty("name").GetString() ?? "";
        var imageUrl = "";
        if (result.TryGetProperty("image_url", out var img))
            imageUrl = img.GetString() ?? "";

        return new SearchResult
        {
            Url = url,
            Title = name,
            Thumbnail = string.IsNullOrEmpty(imageUrl) ? null : imageUrl,
            Engine = "genius",
            Category = SearchCategory.Music,
        };
    }

    private static SearchResult ParseAlbum(JsonElement result)
    {
        var url = result.GetProperty("url").GetString() ?? "";
        var fullTitle = result.GetProperty("full_title").GetString() ?? "";

        var coverArt = "";
        if (result.TryGetProperty("cover_art_url", out var cover))
            coverArt = cover.GetString() ?? "";

        var content = "";
        if (result.TryGetProperty("name_with_artist", out var nwa))
            content = nwa.GetString() ?? "";
        if (string.IsNullOrEmpty(content) && result.TryGetProperty("name", out var n))
            content = n.GetString() ?? "";

        if (result.TryGetProperty("release_date_components", out var rdc)
            && rdc.TryGetProperty("year", out var year))
            content = $"{year} / {content}";

        return new SearchResult
        {
            Url = url,
            Title = fullTitle,
            Content = content.Trim(),
            Thumbnail = string.IsNullOrEmpty(coverArt) ? null : coverArt,
            Engine = "genius",
            Category = SearchCategory.Music,
        };
    }
}
