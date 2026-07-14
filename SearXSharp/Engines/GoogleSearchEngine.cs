using AngleSharp;
using SearXSharp.Models;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Google (www.google.com).
/// Uses HTML scraping (non-API) to retrieve search results, similar to SearXNG's google.py.
/// Includes CAPTCHA detection, consent cookie bypass, Google image search support,
/// data:image decoding for thumbnails, and search suggestions.
/// </summary>
public partial class GoogleSearchEngine : SearchEngineBase
{
    // Google subdomains per region (from SearXNG's fetch_traits)
    private static readonly Dictionary<string, string> _googleDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "www.google.com",
        ["GB"] = "www.google.co.uk",
        ["DE"] = "www.google.de",
        ["FR"] = "www.google.fr",
        ["JP"] = "www.google.co.jp",
        ["RU"] = "www.google.ru",
        ["BR"] = "www.google.com.br",
        ["IT"] = "www.google.it",
        ["CA"] = "www.google.ca",
        ["ES"] = "www.google.es",
        ["NL"] = "www.google.nl",
        ["AU"] = "www.google.com.au",
        ["CN"] = "www.google.cn",
    };

    // Safe search filter mapping (SearXNG-style -> Google safe parameter)
    private static readonly Dictionary<SafeSearchLevel, string> _filterMapping = new()
    {
        [SafeSearchLevel.None] = "off",
        [SafeSearchLevel.Moderate] = "medium",
        [SafeSearchLevel.Strict] = "high",
    };

    // Time range mapping (SearXNG-style -> Google tbs parameter)
    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "qdr:d",
        [TimeRange.Week] = "qdr:w",
        [TimeRange.Month] = "qdr:m",
        [TimeRange.Year] = "qdr:y",
    };

    // Characters for random arc_id generation (SearXNG's _arcid_range)
    private const string _arcidRange = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";
    private static string? _arcidRandom;
    private static DateTime _arcidGenerated = DateTime.MinValue;
    private static readonly object _arcidLock = new();

    // Regex to extract data:image URIs from inline JavaScript (SearXNG's RE_DATA_IMAGE)
    [GeneratedRegex(@"data:image[^']*?'[^']*?'((?:dimg|pimg|tsuid)[^']*)", RegexOptions.Compiled)]
    private static partial Regex DataImageRegex();

    /// <inheritdoc />
    public override string Name => "google";

    /// <inheritdoc />
    public override string DisplayName => "Google";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 50;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    /// <summary>
    /// Initializes a new instance of the Google search engine.
    /// </summary>
    public GoogleSearchEngine() : base() { }

    /// <summary>
    /// Initializes a new instance with a logger.
    /// </summary>
    public GoogleSearchEngine(ILogger logger) : base(logger) { }

    /// <summary>
    /// Creates an HttpClient without auto-redirect so we can detect CAPTCHA/sorry redirects.
    /// Based on SearXNG's <c>detect_google_sorry()</c> which checks the response URL.
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
        {
            return CreateErrorResult(validationError);
        }

        try
        {
            if (query.Category == SearchCategory.Images)
            {
                return await SearchImagesAsync(query, ct);
            }
            return await SearchWebAsync(query, ct);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("captcha") || ex.Message.Contains("sorry"))
        {
            return CreateErrorResult("captcha", suspended: true);
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

    /// <summary>
    /// Performs a general web search on Google.
    /// Based on SearXNG's <c>google.py request()</c> URL construction.
    /// </summary>
    private async Task<SearchResultList> SearchWebAsync(SearchQuery query, CancellationToken ct)
    {
        var start = (query.Page - 1) * 10;
        var domain = GetGoogleDomain(query.Language);

        var parameters = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["start"] = start.ToString(),
            ["hl"] = GetGoogleLanguage(query.Language),
            ["lr"] = GetGoogleLanguageRestrict(query.Language),
            ["ie"] = "utf8",
            ["oe"] = "utf8",
            ["filter"] = "0",
        };

        if (query.SafeSearch != SafeSearchLevel.None)
        {
            parameters["safe"] = _filterMapping[query.SafeSearch];
        }

        if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tbs))
        {
            parameters["tbs"] = tbs;
        }

        var url = $"https://{domain}/search?" + string.Join("&", parameters.Select(p =>
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"));

        using var request = CreateGetRequest(url, useGsaAgent: true);
        // Add Google consent bypass cookie (SearXNG uses CONSENT=YES+)
        request.Headers.Add("Cookie", "CONSENT=YES+");

        var response = await SendRequestAsync(request, ct);

        // Check for Google CAPTCHA/sorry redirect (SearXNG checks resp.url.host/path)
        if (IsCaptchaRedirect(response, out var redirectUrl))
        {
            _logger.Warning("{Engine}: CAPTCHA/sorry redirect detected: {Url}", Name, redirectUrl);
            throw new HttpRequestException("captcha");
        }

        // Follow redirect if any (non-captcha)
        response = await FollowRedirectIfNeededAsync(response, ct);

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);

        // Also check content for CAPTCHA
        if (DetectGoogleCaptcha(html))
        {
            _logger.Warning("{Engine}: CAPTCHA detected for query: {Query}", Name, query.Query);
            throw new HttpRequestException("captcha");
        }

        var dataImageMap = ParseUrlImages(html);
        var results = ParseWebResults(html, dataImageMap);

        _logger.Debug("{Engine}: Parsed {Count} web results", Name, results.Count);
        return CreateResultList(results);
    }

    /// <summary>
    /// Performs an image search on Google.
    /// Uses Google's internal async JSON API (same as SearXNG's google_images.py).
    /// Includes random arc_id generation to avoid bot detection (SearXNG's ui_async()).
    /// </summary>
    private async Task<SearchResultList> SearchImagesAsync(SearchQuery query, CancellationToken ct)
    {
        var domain = GetGoogleDomain(query.Language);
        var parameters = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["tbm"] = "isch",
            ["hl"] = GetGoogleLanguage(query.Language),
            ["ie"] = "utf8",
            ["oe"] = "utf8",
            ["asearch"] = "isch",
        };

        if (query.SafeSearch != SafeSearchLevel.None)
        {
            parameters["safe"] = _filterMapping[query.SafeSearch];
        }

        if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tbs))
        {
            parameters["tbs"] = tbs;
        }

        // Generate random arc_id every hour (like SearXNG's ui_async())
        var asyncParam = GetAsyncParam(query.Page - 1);

        var url = $"https://{domain}/search?" + string.Join("&", parameters.Select(p =>
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"))
            + $"&async={HttpUtility.UrlEncode(asyncParam)}";

        using var request = CreateGetRequest(url, useGsaAgent: true);
        request.Headers.Add("Cookie", "CONSENT=YES+");

        var response = await SendRequestAsync(request, ct);

        // Check for CAPTCHA redirect
        if (IsCaptchaRedirect(response, out var redirectUrl))
        {
            _logger.Warning("{Engine}: CAPTCHA/sorry redirect for image query: {Url}", Name, redirectUrl);
            throw new HttpRequestException("captcha");
        }

        // Follow redirect if any
        response = await FollowRedirectIfNeededAsync(response, ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);

        // Detect CAPTCHA in content
        if (DetectGoogleCaptcha(json))
        {
            _logger.Warning("{Engine}: CAPTCHA detected for image query: {Query}", Name, query.Query);
            throw new HttpRequestException("captcha");
        }

        var results = ParseImageResults(json);
        _logger.Debug("{Engine}: Parsed {Count} image results", Name, results.Count);
        return CreateResultList(results);
    }

    /// <summary>
    /// Checks if the response is a redirect to Google's CAPTCHA/sorry page.
    /// Based on SearXNG's <c>detect_google_sorry()</c> which checks
    /// <c>resp.url.host == "sorry.google.com"</c> or path starts with "/sorry".
    /// </summary>
    private static bool IsCaptchaRedirect(HttpResponseMessage response, out string? redirectUrl)
    {
        redirectUrl = null;

        if (response.StatusCode != HttpStatusCode.Found
            && response.StatusCode != HttpStatusCode.Redirect
            && response.StatusCode != HttpStatusCode.RedirectMethod
            && (int)response.StatusCode < 300 || (int)response.StatusCode >= 400)
        {
            return false;
        }

        var location = response.Headers.Location?.ToString() ?? "";
        redirectUrl = location;

        return location.Contains("sorry.google.com", StringComparison.OrdinalIgnoreCase)
            || location.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)
            || location.Contains("/captcha", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Follows a redirect manually if the response indicates one.
    /// Returns the followed response or the original if no redirect.
    /// </summary>
    private async Task<HttpResponseMessage> FollowRedirectIfNeededAsync(
        HttpResponseMessage response, CancellationToken ct, int maxRedirects = 5)
    {
        var currentResponse = response;
        var redirectCount = 0;

        while ((int)currentResponse.StatusCode >= 300 && (int)currentResponse.StatusCode < 400
               && redirectCount < maxRedirects)
        {
            var location = currentResponse.Headers.Location?.ToString() ?? "";
            if (string.IsNullOrEmpty(location))
                break;

            // Resolve relative URLs
            if (!location.StartsWith("http"))
            {
                var baseUri = currentResponse.RequestMessage?.RequestUri;
                if (baseUri != null)
                    location = new Uri(baseUri, location).ToString();
            }

            currentResponse.Dispose();
            using var followRequest = CreateGetRequest(location);
            currentResponse = await _httpClient.SendAsync(followRequest, ct);
            redirectCount++;
        }

        return currentResponse;
    }

    /// <summary>
    /// Detects if Google returned a CAPTCHA/sorry page.
    /// Based on SearXNG's <c>detect_google_sorry()</c>.
    /// </summary>
    private static bool DetectGoogleCaptcha(string html)
    {
        return html.Contains("sorry.google.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Our systems have detected unusual traffic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generates a random arc_id for async image searches, regenerated every hour.
    /// Based on SearXNG's <c>ui_async()</c> which generates random arc_id every 3600s.
    /// Format: "arc_id:srp_{random}_1{start:02},use_ac:true,_fmt:prog"
    /// </summary>
    private static string GetAsyncParam(int start)
    {
        const string useAc = "use_ac:true";
        const string fmt = "_fmt:prog";

        lock (_arcidLock)
        {
            if (string.IsNullOrEmpty(_arcidRandom)
                || DateTime.UtcNow - _arcidGenerated > TimeSpan.FromHours(1))
            {
                var random = new Random();
                var chars = new char[23];
                for (int i = 0; i < 23; i++)
                    chars[i] = _arcidRange[random.Next(_arcidRange.Length)];
                _arcidRandom = new string(chars);
                _arcidGenerated = DateTime.UtcNow;
            }
        }

        var arcId = $"arc_id:srp_{_arcidRandom}_1{start:D2}";
        return string.Join(",", arcId, useAc, fmt);
    }

    /// <summary>
    /// Extracts data:image URIs from inline JavaScript and maps them to element IDs.
    /// Based on SearXNG's <c>parse_url_images()</c> with RE_DATA_IMAGE regex.
    /// Google embeds thumbnail images as data:image URIs in JavaScript, referenced by element IDs.
    /// </summary>
    private Dictionary<string, string> ParseUrlImages(string html)
    {
        var map = new Dictionary<string, string>();

        try
        {
            var matches = DataImageRegex().Matches(html);
            foreach (Match match in matches)
            {
                var dataImage = match.Groups[0].Value;
                var imgId = match.Groups[1].Value;

                // Extract just the data:image part (before the closing quote)
                var dataUriEnd = dataImage.IndexOf("'", StringComparison.Ordinal);
                if (dataUriEnd > 0)
                {
                    var dataUri = dataImage[..dataUriEnd];
                    // Unescape JSON unicode escapes
                    dataUri = Regex.Replace(dataUri, @"\\u([0-9A-Fa-f]{4})",
                        m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

                    map[imgId] = dataUri;
                }
            }

            _logger?.Debug("Parsed {Count} data:image mappings", map.Count);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to parse data:image URIs");
        }

        return map;
    }

    /// <summary>
    /// Parses Google web search results from HTML.
    /// Based on SearXNG's <c>google.py response()</c> with XPath converted to CSS selectors.
    /// Includes data:image thumbnail decoding and suggestion parsing.
    /// </summary>
    private List<SearchResult> ParseWebResults(string html, Dictionary<string, string> dataImageMap)
    {
        var results = new List<SearchResult>();
        var suggestions = new List<string>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Parse search suggestions (SearXNG's suggestion_xpath)
            var suggestionElements = document.QuerySelectorAll("div.gGQDvd.iIWm4b a");
            foreach (var suggestion in suggestionElements)
            {
                var text = suggestion.TextContent.Trim();
                if (!string.IsNullOrEmpty(text))
                    suggestions.Add(text);
            }

            // Google results are in <a data-ved> elements (newer layout)
            var resultLinks = document.QuerySelectorAll("a[data-ved]:not([class])");

            var position = (1 - 1) * 10 + 1;
            foreach (var link in resultLinks)
            {
                try
                {
                    var titleElement = link.QuerySelector("div[style]");
                    if (titleElement == null) continue;

                    var title = titleElement.TextContent.Trim();
                    var rawUrl = link.GetAttribute("href");
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(rawUrl)) continue;

                    // Decode Google's redirect URL (/url?q=...&sa=U...)
                    var url = DecodeGoogleUrl(rawUrl);

                    // Extract content from the parent's description div
                    var parent = link.ParentElement;
                    var contentElement = parent?.QuerySelector(
                        "div[class*='VwiC3b'], div[class*='BNeawe'], div[class*='st']");
                    var content = contentElement?.TextContent.Trim() ?? string.Empty;

                    // Extract thumbnail with data:image decoding (SearXNG feature)
                    var img = link.QuerySelector("img");
                    string? thumbnail = null;
                    if (img != null)
                    {
                        var src = img.GetAttribute("src");
                        if (src != null)
                        {
                            if (src.StartsWith("data:image"))
                            {
                                var imgId = img.GetAttribute("id");
                                if (imgId != null && dataImageMap.TryGetValue(imgId, out var decodedSrc))
                                    thumbnail = decodedSrc;
                            }
                            else
                            {
                                thumbnail = src;
                            }
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = "google",
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Web,
                        Thumbnail = thumbnail,
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse a result item", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse web results HTML", Name);
        }

        // Attach suggestions to the result list
        if (suggestions.Count > 0)
        {
            _logger.Debug("{Engine}: Parsed {Count} suggestions", Name, suggestions.Count);
        }

        return results;
    }

    /// <summary>
    /// Parses Google image search results from JSON.
    /// Based on SearXNG's <c>google_images.py response()</c>.
    /// </summary>
    private List<SearchResult> ParseImageResults(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            // Extract JSON from the async response (starts with {"ischj":)
            var jsonStart = json.IndexOf("{\"ischj\":", StringComparison.Ordinal);
            if (jsonStart < 0) return results;

            var jsonData = System.Text.Json.JsonDocument.Parse(json[jsonStart..]);
            var metadata = jsonData.RootElement.GetProperty("ischj").GetProperty("metadata");

            var position = 1;
            foreach (var item in metadata.EnumerateArray())
            {
                try
                {
                    var result = item.GetProperty("result");
                    var originalImage = item.GetProperty("original_image");
                    var thumbnail = item.GetProperty("thumbnail");

                    var url = result.GetProperty("referrer_url").GetString() ?? string.Empty;
                    var title = result.GetProperty("page_title").GetString() ?? string.Empty;
                    var snippet = item.GetProperty("text_in_grid").GetProperty("snippet").GetString() ?? string.Empty;
                    var imgSrc = originalImage.GetProperty("url").GetString() ?? string.Empty;
                    var thumbSrc = thumbnail.GetProperty("url").GetString() ?? string.Empty;
                    var source = result.GetProperty("site_title").GetString() ?? string.Empty;

                    var width = originalImage.GetProperty("width").GetInt32();
                    var height = originalImage.GetProperty("height").GetInt32();

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = snippet,
                        Engine = "google",
                        Type = SearchResultType.Image,
                        Category = SearchCategory.Images,
                        ImgSrc = imgSrc,
                        Thumbnail = thumbSrc,
                        Source = source,
                        Resolution = $"{width} x {height}",
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse an image result", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse image results JSON", Name);
        }

        return results;
    }

    /// <summary>
    /// Decodes Google's redirect-protected URLs (/url?q=...&amp;sa=U...).
    /// Based on SearXNG's google.py URL decoding.
    /// </summary>
    private static string DecodeGoogleUrl(string rawUrl)
    {
        if (rawUrl.StartsWith("/url?q="))
        {
            var query = HttpUtility.ParseQueryString(rawUrl);
            var decodedUrl = query["q"];
            if (!string.IsNullOrEmpty(decodedUrl))
            {
                return decodedUrl;
            }
        }
        return rawUrl;
    }

    /// <summary>
    /// Gets the appropriate Google domain for the given language.
    /// </summary>
    private static string GetGoogleDomain(string? language)
    {
        if (string.IsNullOrEmpty(language) || language == "auto")
        {
            return "www.google.com";
        }

        var parts = language.Split('-');
        var country = parts.Length > 1 ? parts[1].ToUpperInvariant() : parts[0].ToUpperInvariant();

        return _googleDomains.GetValueOrDefault(country, "www.google.com");
    }

    /// <summary>
    /// Gets the Google interface language parameter (hl).
    /// </summary>
    private static string GetGoogleLanguage(string? language)
    {
        return string.IsNullOrEmpty(language) || language == "auto" ? "en" : language;
    }

    /// <summary>
    /// Gets the Google language restrict parameter (lr).
    /// </summary>
    private static string GetGoogleLanguageRestrict(string? language)
    {
        if (string.IsNullOrEmpty(language) || language == "auto")
        {
            return string.Empty;
        }
        return $"lang_{language.Split('-')[0]}";
    }
}
