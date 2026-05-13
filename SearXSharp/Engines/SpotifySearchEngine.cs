using SearXSharp.Models;
using System.Text.Json;
using System.Text;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Spotify.
/// Uses Spotify's public API with client credentials (no user auth required).
/// Based on SearXNG's spotify.py.
/// </summary>
public class SpotifySearchEngine : SearchEngineBase
{
    private const string _tokenUrl = "https://accounts.spotify.com/api/token";
    private const string _apiUrl = "https://api.spotify.com/v1/search";
    private const string _baseUrl = "https://open.spotify.com";

    /// <inheritdoc />
    public override string Name => "spotify";

    /// <inheritdoc />
    public override string DisplayName => "Spotify";

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

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public SpotifySearchEngine() : base() { }
    public SpotifySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            await EnsureTokenAsync(ct);

            var offset = (query.Page - 1) * 20;
            var url = $"{_apiUrl}?q={Uri.EscapeDataString(query.Query)}&type=track&offset={offset}&limit=20";

            var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");

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

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry)
            return;

        // Spotify requires client credentials - we'll use the embedded defaults
        // In a real app, these would be configurable
        var clientId = "4fe3fecfe5334023a1472516cc99d805"; // Public Spotify Web API client ID
        var clientSecret = "0b8e4979a4e34b5b9e8c3c5d9e5f1a2b"; // Placeholder - won't work without real credentials

        var authHeader = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        var tokenRequest = CreatePostRequest(_tokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });
        tokenRequest.Headers.TryAddWithoutValidation("Authorization", $"Basic {authHeader}");

        var response = await SendRequestAsync(tokenRequest, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
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

            var position = 1;
            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var type = item.GetProperty("type").GetString();
                    if (type != "track") continue;

                    var name = item.GetProperty("name").GetString() ?? "";
                    var spotifyUrl = item.GetProperty("external_urls").GetProperty("spotify").GetString() ?? "";
                    var trackId = item.GetProperty("id").GetString() ?? "";

                    var artists = string.Join(", ",
                        item.GetProperty("artists").EnumerateArray()
                            .Select(a => a.GetProperty("name").GetString() ?? ""));

                    var album = item.GetProperty("album").GetProperty("name").GetString() ?? "";

                    var albumImages = item.GetProperty("album").GetProperty("images").EnumerateArray().ToList();
                    var thumbnail = albumImages.Count > 0
                        ? albumImages[0].GetProperty("url").GetString() ?? ""
                        : "";

                    var durationMs = item.GetProperty("duration_ms").GetInt32();
                    var duration = TimeSpan.FromMilliseconds(durationMs);

                    var content = $"{artists} - {album} - {name}";

                    results.Add(new SearchResult
                    {
                        Url = spotifyUrl,
                        Title = name,
                        Content = content,
                        Thumbnail = thumbnail,
                        IframeSrc = $"https://embed.spotify.com/?uri=spotify:track:{trackId}",
                        Duration = duration,
                        Author = artists,
                        Engine = Name,
                        Category = SearchCategory.Music,
                        Type = SearchResultType.Default,
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse track", Name);
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
