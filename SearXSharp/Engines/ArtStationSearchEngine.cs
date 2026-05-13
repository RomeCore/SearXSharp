using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for ArtStation (artist portfolio platform).
/// Uses ArtStation's internal API with CSRF token handling.
/// Based on SearXNG's artstation.py.
/// </summary>
public partial class ArtStationSearchEngine : SearchEngineBase
{
    private const string _csrfUrl = "https://www.artstation.com/api/v2/csrf_protection/token.json";
    private const string _searchUrl = "https://www.artstation.com/api/v2/search/projects.json";
    private const int _resultsPerPage = 20;

    // Simple in-memory token cache (in production this would be more robust)
    private static string? _cachedPublicToken;
    private static string? _cachedPrivateToken;
    private static DateTime _tokenExpiry = DateTime.MinValue;

    private static readonly Regex _sizeRegex = SizeRegex();

    [GeneratedRegex(@"/\d{6,}/")]
    private static partial Regex SizeRegex();

    /// <inheritdoc />
    public override string Name => "artstation";

    /// <inheritdoc />
    public override string DisplayName => "ArtStation";

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

    public ArtStationSearchEngine() : base() { }
    public ArtStationSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            await EnsureTokensAsync(ct);

            var formData = new Dictionary<string, string>
            {
                ["query"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["per_page"] = _resultsPerPage.ToString(),
                ["sorting"] = "relevance",
                ["pro_first"] = "1",
            };

            var json = JsonSerializer.Serialize(formData);
            var request = CreateJsonPostRequest(_searchUrl, json);
            request.Headers.TryAddWithoutValidation("PUBLIC-CSRF-TOKEN", _cachedPublicToken);
            request.Headers.TryAddWithoutValidation("Cookie", $"PRIVATE-CSRF-TOKEN={_cachedPrivateToken}");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(responseJson);
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

    private async Task EnsureTokensAsync(CancellationToken ct)
    {
        if (_cachedPublicToken != null && _cachedPrivateToken != null && DateTime.UtcNow < _tokenExpiry)
            return;

        var request = CreatePostRequest(_csrfUrl, new Dictionary<string, string>());
        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        _cachedPublicToken = doc.RootElement.GetProperty("public_csrf_token").GetString();

        // Extract private token from cookies
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                if (cookie.StartsWith("PRIVATE-CSRF-TOKEN="))
                {
                    _cachedPrivateToken = cookie.Split(';')[0].Split('=')[1];
                    break;
                }
            }
        }

        _tokenExpiry = DateTime.UtcNow.AddHours(1);
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
                    var title = item.GetProperty("title").GetString() ?? "";
                    var url = item.GetProperty("url").GetString() ?? "";
                    var thumb = item.GetProperty("smaller_square_cover_url").GetString() ?? "";

                    // Build full-size image URL from thumbnail
                    var fullsizeImage = _sizeRegex.Replace(thumb, "/")
                        .Replace("smaller_square", "large");

                    var userName = "";
                    var fullName = "";
                    if (item.TryGetProperty("user", out var user))
                    {
                        userName = user.GetProperty("username").GetString() ?? "";
                        if (user.TryGetProperty("full_name", out var fn))
                            fullName = fn.GetString() ?? "";
                    }

                    var author = $"{userName} ({fullName})".Trim();
                    if (author.EndsWith("()"))
                        author = userName;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        ImgSrc = fullsizeImage,
                        Thumbnail = thumb,
                        Author = author,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse artwork", Name);
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
