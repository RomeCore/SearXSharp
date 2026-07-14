using SearXSharp.Models;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Startpage (www.startpage.com).
/// Privacy-focused search engine that proxies Google results.
/// Uses POST requests with sc-code tokens and cookie-based preferences.
/// Based on SearXNG's startpage.py.
/// </summary>
public partial class StartpageSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.startpage.com";
    private const string _searchUrl = "https://www.startpage.com/sp/search";

    private static string? _scCode;
    private static DateTime _scCodeFetched = DateTime.MinValue;
    private static readonly SemaphoreSlim _scCodeLock = new(1, 1);
    private static readonly TimeSpan _scCodeCacheDuration = TimeSpan.FromHours(1);

    [GeneratedRegex(@"React\.createElement\(UIStartpage\.AppSerp(\w+),\s*\{", RegexOptions.Compiled)]
    private static partial Regex AppSerpRegex();

    /// <inheritdoc />
    public override string Name => "startpage";

    /// <inheritdoc />
    public override string DisplayName => "Startpage";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.News, SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 18;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    /// <summary>
    /// Startpage category: "web", "news" or "images".
    /// </summary>
    public string StartpageCategory { get; set; } = "web";

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "d",
        [TimeRange.Week] = "w",
        [TimeRange.Month] = "m",
        [TimeRange.Year] = "y",
    };

    // Maps SearXNG SafeSearchLevel -> Startpage disable_family_filter cookie value.
    // Based on SearXNG's safesearch_dict = {0: "1", 1: "0", 2: "0"}.
    // Startpage's "disable_family_filter": "1" means filter is OFF (allow explicit),
    // "0" means filter is ON (block explicit).
    private static readonly Dictionary<SafeSearchLevel, string> _safeSearchMap = new()
    {
        [SafeSearchLevel.None] = "1",     // None  (0) -> disable filter (= "1", allow everything)
        [SafeSearchLevel.Moderate] = "0", // Mod   (1) -> enable filter (= "0")
        [SafeSearchLevel.Strict] = "0",   // Strict(2) -> enable filter (= "0")
    };

    public StartpageSearchEngine() : base() { }
    public StartpageSearchEngine(ILogger logger) : base(logger) { }

    /// <summary>
    /// Creates an HttpClient without auto-redirect so we can detect CAPTCHA redirects.
    /// Based on SearXNG's CAPTCHA detection via redirect to /sp/captcha.
    /// </summary>
    protected override HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                     | System.Net.DecompressionMethods.Deflate
                                     | System.Net.DecompressionMethods.Brotli,
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en,en-US;q=0.7,en;q=0.3");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        client.DefaultRequestHeaders.Add("DNT", "1");
        client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        client.Timeout = TimeSpan.FromSeconds(Timeout > 0 ? Timeout + 5 : 35);

        return client;
    }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            // Get or refresh sc-code
            var scCode = await GetScCodeAsync(ct);
            if (string.IsNullOrEmpty(scCode))
            {
                _logger.Warning("{Engine}: No sc-code available, CAPTCHA likely", Name);
                return CreateErrorResult("captcha_required");
            }

            // Use language from query or fallback to English
            var lang = string.IsNullOrEmpty(query.Language) || query.Language == "auto"
                ? "en"
                : query.Language;

            // Build form data
            var formData = new Dictionary<string, string>
            {
                ["query"] = query.Query,
                ["cat"] = StartpageCategory,
                ["t"] = "device",
                ["sc"] = scCode,
                ["abp"] = "1",
                ["abd"] = "1",
                ["abe"] = "1",
                ["language"] = lang,
                ["lui"] = lang,
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tr))
                formData["with_date"] = tr;

            if (query.Page > 1)
            {
                formData["page"] = query.Page.ToString();
                formData["segment"] = "startpage.udog";
            }

            // Build cookie with language/region support
            var cookieParts = new List<string>
            {
                "date_timeEEEworld",
                $"disable_family_filterEEE{_safeSearchMap.GetValueOrDefault(query.SafeSearch, "1")}",
                "disable_open_in_new_windowEEE0",
                "enable_post_methodEEE1",
                "enable_proxy_safety_suggestEEE1",
                "enable_stay_controlEEE1",
                "instant_answersEEE1",
                $"lang_homepageEEEs/device/{lang}/",
                "num_of_resultsEEE10",
                "suggestionsEEE1",
                "wt_unitEEEcelsius",
                $"languageEEE{lang}",
                $"language_uiEEE{lang}",
                $"search_results_regionEEEen-US",
            };
            var cookieValue = "N1N" + string.Join("N1N", cookieParts);

            using var request = CreatePostRequest(_searchUrl, formData);
            request.Headers.TryAddWithoutValidation("Origin", _baseUrl);
            request.Headers.TryAddWithoutValidation("Referer", _baseUrl + "/");
            request.Headers.TryAddWithoutValidation("Cookie", $"preferences={Uri.EscapeDataString(cookieValue)}");

            var response = await SendRequestAsync(request, ct);

            // Check for CAPTCHA redirect (SearXNG checks Location header for /sp/captcha)
            if (response.StatusCode == HttpStatusCode.Found
                || response.StatusCode == HttpStatusCode.Redirect
                || response.StatusCode == HttpStatusCode.RedirectMethod)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                if (location.Contains("/sp/captcha", StringComparison.OrdinalIgnoreCase)
                    || location.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warning("{Engine}: CAPTCHA redirect detected: {Location}", Name, location);
                    return CreateErrorResult("captcha_required", suspended: true);
                }

                // Follow redirect manually for non-captcha redirects
                using var followRequest = CreateGetRequest(
                    location.StartsWith("http") ? location : _baseUrl + location);
                response = await _httpClient.SendAsync(followRequest, ct);
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            // Also check response body for captcha indicators
            if (html.Contains("/sp/captcha", StringComparison.OrdinalIgnoreCase)
                || html.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("{Engine}: CAPTCHA detected in response body", Name);
                return CreateErrorResult("captcha_required", suspended: true);
            }

            return ParseResults(html);
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

    private SearchResultList ParseResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            // Startpage embeds results in a React.createElement() call
            var match = AppSerpRegex().Match(html);
            if (!match.Success)
            {
                _logger.Warning("{Engine}: Could not find AppSerp data in response", Name);
                return CreateResultList(results);
            }

            var categ = match.Groups[1].Value;

            // Find the JSON object after the function call
            var startIdx = match.Index + match.Value.Length;
            var braceDepth = 0;
            var jsonStart = -1;
            var jsonEnd = -1;

            for (int i = startIdx; i < html.Length; i++)
            {
                if (html[i] == '{')
                {
                    if (braceDepth == 0) jsonStart = i;
                    braceDepth++;
                }
                else if (html[i] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0 && jsonStart >= 0)
                    {
                        jsonEnd = i + 1;
                        break;
                    }
                }
            }

            if (jsonStart < 0 || jsonEnd < 0)
                return CreateResultList(results);

            var json = html[jsonStart..jsonEnd];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("render", out var render)
                || !render.TryGetProperty("presenter", out var presenter)
                || !presenter.TryGetProperty("regions", out var regions))
                return CreateResultList(results);

            if (!regions.TryGetProperty("mainline", out var mainline))
                return CreateResultList(results);

            foreach (var region in mainline.EnumerateArray())
            {
                if (!region.TryGetProperty("results", out var regionResults))
                    continue;

                var displayType = region.GetProperty("display_type").GetString() ?? "";

                foreach (var item in regionResults.EnumerateArray())
                {
                    try
                    {
                        SearchResult? result = null;

                        if (displayType.Contains("web"))
                            result = ParseWebItem(item);
                        else if (displayType.Contains("news"))
                            result = ParseNewsItem(item);
                        else if (displayType.Contains("image"))
                            result = ParseImageItem(item);

                        if (result != null)
                            results.Add(result);
                    }
                    catch { /* skip */ }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse results JSON", Name);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse results", Name);
        }

        return CreateResultList(results);
    }

    private static SearchResult? ParseWebItem(JsonElement item)
    {
        var url = "";
        if (item.TryGetProperty("clickUrl", out var clickUrl))
            url = clickUrl.GetString() ?? "";

        var title = "";
        if (item.TryGetProperty("title", out var titleEl))
            title = StripHtml(titleEl.GetString() ?? "");

        var content = "";
        if (item.TryGetProperty("description", out var desc))
            content = StripHtml(desc.GetString() ?? "");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            return null;

        // Try to extract date from content
        DateTime? publishedDate = null;
        var dateMatch = Regex.Match(content, @"^(\d{1,2} \w{3} \d{4}) \.\.\. ");
        if (dateMatch.Success)
        {
            if (DateTime.TryParse(dateMatch.Groups[1].Value, out var dt))
                publishedDate = dt;
            content = content.Substring(dateMatch.Index + dateMatch.Value.Length);
        }

        return new SearchResult
        {
            Url = url,
            Title = title,
            Content = content,
            PublishedDate = publishedDate,
            Engine = "startpage",
            Category = SearchCategory.Web,
        };
    }

    private static SearchResult? ParseNewsItem(JsonElement item)
    {
        var url = "";
        if (item.TryGetProperty("clickUrl", out var clickUrl))
            url = clickUrl.GetString() ?? "";

        var title = "";
        if (item.TryGetProperty("title", out var titleEl))
            title = StripHtml(titleEl.GetString() ?? "");
        title = RemovePua(title);

        var content = "";
        if (item.TryGetProperty("description", out var desc))
            content = StripHtml(desc.GetString() ?? "");
        content = RemovePua(content);

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            return null;

        DateTime? publishedDate = null;
        if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Number)
            publishedDate = DateTimeOffset.FromUnixTimeMilliseconds(dateEl.GetInt64()).DateTime;

        string? thumbnail = null;
        if (item.TryGetProperty("thumbnailUrl", out var thumbEl))
            thumbnail = _baseUrl + thumbEl.GetString();

        return new SearchResult
        {
            Url = url,
            Title = title,
            Content = content,
            PublishedDate = publishedDate,
            Thumbnail = thumbnail,
            Engine = "startpage",
            Category = SearchCategory.News,
            Type = SearchResultType.News,
        };
    }

    private static SearchResult? ParseImageItem(JsonElement item)
    {
        var url = "";
        if (item.TryGetProperty("altClickUrl", out var altUrl))
            url = altUrl.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(url))
            return null;

        var title = "";
        if (item.TryGetProperty("title", out var titleEl))
            title = StripHtml(titleEl.GetString() ?? "");

        var imgSrc = "";
        if (item.TryGetProperty("rawImageUrl", out var rawUrl))
            imgSrc = rawUrl.GetString() ?? "";

        string? thumbnail = null;
        if (item.TryGetProperty("thumbnailUrl", out var thumbEl))
            thumbnail = _baseUrl + thumbEl.GetString();

        var resolution = "";
        if (item.TryGetProperty("width", out var w) && item.TryGetProperty("height", out var h))
            resolution = $"{w.GetInt32()}x{h.GetInt32()}";

        return new SearchResult
        {
            Url = url,
            Title = title,
            ImgSrc = imgSrc,
            Thumbnail = thumbnail,
            Resolution = resolution,
            Engine = "startpage",
            Category = SearchCategory.Images,
            Type = SearchResultType.Image,
        };
    }

    private async Task<string?> GetScCodeAsync(CancellationToken ct)
    {
        // Check cache
        if (_scCode != null && DateTime.UtcNow - _scCodeFetched < _scCodeCacheDuration)
            return _scCode;

        await _scCodeLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_scCode != null && DateTime.UtcNow - _scCodeFetched < _scCodeCacheDuration)
                return _scCode;

            var url = _baseUrl + "/";
            using var request = CreateGetRequest(url);
            var response = await _httpClient.SendAsync(request, ct);

            // Check for captcha redirect during sc-code fetch (SearXNG checks this)
            if (response.StatusCode == HttpStatusCode.Found
                || response.StatusCode == HttpStatusCode.Redirect)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                if (location.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warning("{Engine}: CAPTCHA during sc-code fetch", Name);
                    return null;
                }
                // Follow redirect
                using var followRequest = CreateGetRequest(
                    location.StartsWith("http") ? location : _baseUrl + location);
                response = await _httpClient.SendAsync(followRequest, ct);
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);

            // Parse sc-code from search form
            var formMatch = Regex.Match(html, @"<form[^>]*id=""search""[^>]*>");
            if (!formMatch.Success)
            {
                _logger.Warning("{Engine}: Could not find search form (CAPTCHA?)", Name);
                return null;
            }

            var scMatch = Regex.Match(html, @"<input[^>]*name=""sc""[^>]*value=""([^""]*)""");
            if (!scMatch.Success)
            {
                _logger.Warning("{Engine}: Could not find sc-code in search form", Name);
                return null;
            }

            _scCode = scMatch.Groups[1].Value;
            _scCodeFetched = DateTime.UtcNow;
            _logger.Debug("{Engine}: Got new sc-code: {ScCode}", Name, _scCode);
            return _scCode;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to fetch sc-code", Name);
            return null;
        }
        finally
        {
            _scCodeLock.Release();
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        return Regex.Replace(html, "<[^>]*>", "").Trim();
    }

    private static string RemovePua(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return new string(text.Where(c => c < 0xF8FF || c > 0xFFFF).ToArray());
    }
}
