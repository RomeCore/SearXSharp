using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Pexels (royalty-free images).
/// Extracts secret API key from the website, then uses the internal API.
/// Based on SearXNG's pexels.py with secret key caching.
/// </summary>
public class PexelsSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.pexels.com";
    private const string _fallbackApiKey = "H2jk9uKnhRmL6WPwh89zBezWvr";

    private static string? _cachedSecretKey;
    private static readonly SemaphoreSlim _keyLock = new(1, 1);

    private static readonly Regex _secretKeyRegex = new(
        "\"secret-key\":\\s*\"(.*?)\"", RegexOptions.Compiled);

    /// <inheritdoc />
    public override string Name => "pexels";

    /// <inheritdoc />
    public override string DisplayName => "Pexels";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 20;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "last_24_hours",
        [TimeRange.Week] = "last_week",
        [TimeRange.Month] = "last_month",
        [TimeRange.Year] = "last_year",
    };

    public PexelsSearchEngine() : base() { }
    public PexelsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var secretKey = await GetSecretKeyAsync(ct);

            var args = new Dictionary<string, string>
            {
                ["query"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["per_page"] = "20",
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var dateFrom))
                args["date_from"] = dateFrom;

            var url = $"{_baseUrl}/en-us/api/v3/search/photos?"
                + string.Join("&", args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("secret-key", secretKey);
            request.Headers.Referrer = new Uri(_baseUrl);

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

    private async Task<string> GetSecretKeyAsync(CancellationToken ct)
    {
        if (_cachedSecretKey != null)
            return _cachedSecretKey;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_cachedSecretKey != null)
                return _cachedSecretKey;

            try
            {
                // Fetch main page to extract secret key from scripts
                var request = CreateGetRequest(_baseUrl);
                request.Headers.Referrer = new Uri(_baseUrl);

                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(ct);

                var context = BrowsingContext.New(Configuration.Default);
                var doc = context.OpenAsync(req => req.Content(html)).Result;

                foreach (var script in doc.QuerySelectorAll("script"))
                {
                    var src = script.GetAttribute("src");
                    if (string.IsNullOrEmpty(src)) continue;

                    try
                    {
                        var scriptRequest = CreateGetRequest(src);
                        scriptRequest.Headers.Referrer = new Uri(_baseUrl);
                        var scriptResponse = await _httpClient.SendAsync(scriptRequest, ct);
                        scriptResponse.EnsureSuccessStatusCode();
                        var scriptContent = await scriptResponse.Content.ReadAsStringAsync(ct);

                        var match = _secretKeyRegex.Match(scriptContent);
                        if (match.Success)
                        {
                            _cachedSecretKey = match.Groups[1].Value;
                            _logger.Information("{Engine}: Successfully extracted secret key", Name);
                            return _cachedSecretKey;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "{Engine}: Failed to extract secret key, using fallback", Name);
            }

            _cachedSecretKey = _fallbackApiKey;
            _logger.Information("{Engine}: Using fallback API key", Name);
            return _cachedSecretKey;
        }
        finally
        {
            _keyLock.Release();
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
                    var attrs = item.GetProperty("attributes");
                    var slug = attrs.GetProperty("slug").GetString() ?? "";
                    var id = attrs.GetProperty("id").GetInt32();
                    var title = attrs.GetProperty("title").GetString() ?? "";
                    var description = "";
                    if (attrs.TryGetProperty("description", out var desc))
                        description = desc.GetString() ?? "";

                    var image = attrs.GetProperty("image");
                    var thumbnailSrc = image.GetProperty("small").GetString() ?? "";
                    var downloadLink = image.GetProperty("download_link").GetString() ?? "";

                    var width = attrs.GetProperty("width").GetInt32();
                    var height = attrs.GetProperty("height").GetInt32();
                    var resolution = $"{width}x{height}";

                    var author = "";
                    if (attrs.TryGetProperty("user", out var user)
                        && user.TryGetProperty("username", out var username))
                        author = username.GetString() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = $"{_baseUrl}/photo/{slug}-{id}/",
                        Title = title,
                        Content = description,
                        Thumbnail = thumbnailSrc,
                        ImgSrc = downloadLink,
                        Resolution = resolution,
                        Author = author,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result item", Name);
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
