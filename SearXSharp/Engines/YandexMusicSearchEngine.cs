using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Yandex Music (music.yandex.ru).
/// Uses the Yandex Music internal search API (no key required).
/// Based on SearXNG's yandex_music.py.
/// </summary>
public class YandexMusicSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://music.yandex.ru";
    private const string _searchUrl = _baseUrl + "/handlers/music-search.jsx";

    /// <inheritdoc />
    public override string Name => "yandex_music";

    /// <inheritdoc />
    public override string DisplayName => "Yandex Music";

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

    public YandexMusicSearchEngine() : base() { }
    public YandexMusicSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, string>
            {
                ["text"] = query.Query,
                ["page"] = (query.Page - 1).ToString(),
            };

            var url = _searchUrl + "?" + string.Join("&",
                args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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

            if (!root.TryGetProperty("tracks", out var tracks)
                || !tracks.TryGetProperty("items", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var type = item.GetProperty("type").GetString() ?? "";
                    if (type != "music") continue;

                    var trackId = item.GetProperty("id").GetString() ?? "";
                    var title = item.GetProperty("title").GetString() ?? "";

                    var albumId = "";
                    var albumTitle = "";
                    if (item.TryGetProperty("albums", out var albums) && albums.GetArrayLength() > 0)
                    {
                        albumId = albums[0].GetProperty("id").GetString() ?? "";
                        albumTitle = albums[0].GetProperty("title").GetString() ?? "";
                    }

                    var artistName = "";
                    if (item.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
                    {
                        artistName = artists[0].GetProperty("name").GetString() ?? "";
                    }

                    var url = $"{_baseUrl}/album/{albumId}/track/{trackId}";
                    var iframeSrc = $"{_baseUrl}/iframe/track/{trackId}/{albumId}";
                    var content = $"[{albumTitle}] {artistName} - {title}";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        IframeSrc = iframeSrc,
                        Author = artistName,
                        Engine = Name,
                        Category = SearchCategory.Music,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse track item", Name);
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
