using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Deezer (music streaming).
/// Uses the official Deezer API (no key required).
/// Based on SearXNG's deezer.py.
/// </summary>
public class DeezerSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.deezer.com/search?q={query}&index={offset}";
    private const string _iframeSrc = "https://www.deezer.com/plugins/player?type=tracks&id={audioid}";

    /// <inheritdoc />
    public override string Name => "deezer";

    /// <inheritdoc />
    public override string DisplayName => "Deezer";

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

    public DeezerSearchEngine() : base() { }
    public DeezerSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var offset = (query.Page - 1) * 25;
            var url = _searchUrl
                .Replace("{query}", Uri.EscapeDataString(query.Query))
                .Replace("{offset}", offset.ToString());

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
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            foreach (var track in data.EnumerateArray())
            {
                try
                {
                    if (track.GetProperty("type").GetString() != "track")
                        continue;

                    var title = track.GetProperty("title").GetString() ?? "";
                    var url = track.GetProperty("link").GetString() ?? "";

                    // Ensure HTTPS
                    if (url.StartsWith("http://"))
                        url = "https" + url[4..];

                    var artistName = track.GetProperty("artist").GetProperty("name").GetString() ?? "";
                    var albumTitle = track.GetProperty("album").GetProperty("title").GetString() ?? "";
                    var trackId = track.GetProperty("id").GetInt32();

                    var content = $"{artistName} - {albumTitle} - {title}";
                    var iframeSrc = _iframeSrc.Replace("{audioid}", trackId.ToString());

                    // Duration
                    TimeSpan? duration = null;
                    if (track.TryGetProperty("duration", out var durEl))
                        duration = TimeSpan.FromSeconds(durEl.GetInt32());

                    string? thumbnail = null;
                    if (track.TryGetProperty("album", out var album)
                        && album.TryGetProperty("cover_medium", out var cover))
                        thumbnail = cover.GetString();

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        IframeSrc = iframeSrc,
                        Duration = duration,
                        Thumbnail = thumbnail,
                        Author = artistName,
                        Engine = Name,
                        Category = SearchCategory.Music,
                        Type = SearchResultType.Default,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
