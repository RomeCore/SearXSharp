using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for SoundCloud (music streaming).
/// Uses SoundCloud's internal API (api-v2.soundcloud.com).
/// Based on SearXNG's soundcloud.py.
/// </summary>
public partial class SoundCloudSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api-v2.soundcloud.com/search";
    private const string _soundCloudUrl = "https://soundcloud.com";

    private static string? _guestClientId;
    private static readonly SemaphoreSlim _clientIdLock = new(1, 1);

    [GeneratedRegex(@"client_id:""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ClientIdRegex();

    /// <inheritdoc />
    public override string Name => "soundcloud";

    /// <inheritdoc />
    public override string DisplayName => "SoundCloud";

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

    public SoundCloudSearchEngine() : base() { }
    public SoundCloudSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            // Get or refresh client ID
            if (string.IsNullOrEmpty(_guestClientId))
            {
                await _clientIdLock.WaitAsync(ct);
                try
                {
                    if (string.IsNullOrEmpty(_guestClientId))
                        _guestClientId = await FetchClientIdAsync(ct);
                }
                finally
                {
                    _clientIdLock.Release();
                }
            }

            if (string.IsNullOrEmpty(_guestClientId))
            {
                _logger.Warning("{Engine}: No client ID available", Name);
                return CreateErrorResult("no_client_id");
            }

            var offset = (query.Page - 1) * 10;
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["offset"] = offset.ToString(),
                ["limit"] = "10",
                ["facet"] = "model",
                ["client_id"] = _guestClientId,
                ["app_locale"] = "en",
            };

            var url = _searchUrl + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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

            if (!root.TryGetProperty("collection", out var collection))
                return CreateResultList(results);

            foreach (var item in collection.EnumerateArray())
            {
                try
                {
                    var kind = item.GetProperty("kind").GetString();
                    if (kind != "track" && kind != "playlist")
                        continue;

                    var permalinkUrl = "";
                    if (item.TryGetProperty("permalink_url", out var urlEl))
                        permalinkUrl = urlEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(permalinkUrl))
                        continue;

                    var title = item.GetProperty("title").GetString() ?? "";
                    var uri = Uri.EscapeDataString(item.GetProperty("uri").GetString() ?? "");

                    var description = "";
                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        description = desc.GetString() ?? "";

                    var labelName = "";
                    if (item.TryGetProperty("label_name", out var label) && label.ValueKind == JsonValueKind.String)
                        labelName = label.GetString() ?? "";

                    var contentParts = new[] { description, labelName }.Where(c => !string.IsNullOrEmpty(c));
                    var content = string.Join(" / ", contentParts);

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("last_modified", out var modified))
                    {
                        if (DateTime.TryParse(modified.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    var iframeSrc = "https://w.soundcloud.com/player/?url=" + uri;

                    var thumbnail = "";
                    if (item.TryGetProperty("artwork_url", out var artwork) && artwork.ValueKind == JsonValueKind.String)
                        thumbnail = artwork.GetString() ?? "";

                    if (string.IsNullOrEmpty(thumbnail) && item.TryGetProperty("user", out var user)
                        && user.TryGetProperty("avatar_url", out var avatar))
                        thumbnail = avatar.GetString() ?? "";

                    TimeSpan? duration = null;
                    if (item.TryGetProperty("duration", out var durEl))
                        duration = TimeSpan.FromMilliseconds(durEl.GetDouble());

                    var views = 0L;
                    if (item.TryGetProperty("playback_count", out var plays))
                        views = plays.GetInt64();

                    var author = "";
                    if (item.TryGetProperty("user", out var u) && u.TryGetProperty("full_name", out var fn))
                        author = fn.GetString() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = permalinkUrl,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        IframeSrc = iframeSrc,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        Duration = duration,
                        Views = views,
                        Author = author,
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

    /// <summary>
    /// Extracts a client_id from SoundCloud's main page or its JavaScript assets.
    /// SoundCloud ties client_id to the session, and it changes periodically.
    /// </summary>
    private async Task<string?> FetchClientIdAsync(CancellationToken ct)
    {
        try
        {
            using var request = CreateGetRequest(_soundCloudUrl);
            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);

            // Find app JS URLs in script tags
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).Result;
            var scriptTags = document.QuerySelectorAll("script[src]");

            var appJsUrls = scriptTags
                .Select(s => s.GetAttribute("src"))
                .Where(src => src != null && src.Contains("/assets/"))
                .Reverse()
                .ToList();

            foreach (var jsUrl in appJsUrls)
            {
                try
                {
                    var fullUrl = jsUrl!.StartsWith("http") ? jsUrl : "https://soundcloud.com" + jsUrl;
                    using var jsRequest = CreateGetRequest(fullUrl);
                    var jsResponse = await _httpClient.SendAsync(jsRequest, ct);

                    if (!jsResponse.IsSuccessStatusCode)
                        continue;

                    var jsContent = await jsResponse.Content.ReadAsStringAsync(ct);
                    var match = ClientIdRegex().Match(jsContent);
                    if (match.Success)
                    {
                        var clientId = match.Groups[1].Value;
                        _logger.Information("{Engine}: Got client_id from JS bundle", Name);
                        return clientId;
                    }
                }
                catch { /* try next JS file */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to fetch client_id", Name);
        }

        return null;
    }
}
