using AngleSharp;
using SearXSharp.Models;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Google (www.google.com).
/// Uses HTML scraping (non-API) to retrieve search results, similar to SearXNG's google.py.
/// Includes CAPTCHA detection, consent cookie bypass, and Google image search support.
/// </summary>
public class GoogleSearchEngine : SearchEngineBase
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
        catch (TaskCanceledException)
        {
            return CreateErrorResult("timeout", suspended: true);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("sorry"))
        {
            return CreateErrorResult("captcha", suspended: true);
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
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);

        // Check for Google CAPTCHA page
        if (DetectGoogleCaptcha(html))
        {
            _logger.Warning("{Engine}: CAPTCHA detected for query: {Query}", Name, query.Query);
            throw new HttpRequestException("sorry");
        }

        var results = ParseWebResults(html);
        _logger.Debug("{Engine}: Parsed {Count} web results", Name, results.Count);
        return CreateResultList(results);
    }

    /// <summary>
    /// Performs an image search on Google.
    /// Uses Google's internal async JSON API (same as SearXNG's google_images.py).
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

        var url = $"https://{domain}/search?" + string.Join("&", parameters.Select(p =>
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"))
            + $"&async=_fmt:json,p:1,ijn:{query.Page - 1}";

        using var request = CreateGetRequest(url, useGsaAgent: true);
        request.Headers.Add("Cookie", "CONSENT=YES+");

        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);

        // Detect CAPTCHA
        if (DetectGoogleCaptcha(json))
        {
            _logger.Warning("{Engine}: CAPTCHA detected for image query: {Query}", Name, query.Query);
            throw new HttpRequestException("sorry");
        }

        var results = ParseImageResults(json);
        _logger.Debug("{Engine}: Parsed {Count} image results", Name, results.Count);
        return CreateResultList(results);
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
    /// Parses Google web search results from HTML.
    /// Based on SearXNG's <c>google.py response()</c> with XPath converted to CSS selectors.
    /// </summary>
    private List<SearchResult> ParseWebResults(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

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
                    var contentElement = parent?.QuerySelector("div[class*='VwiC3b'], div[class*='BNeawe'], div[class*='st'])");
                    var content = contentElement?.TextContent.Trim() ?? string.Empty;

                    // Extract thumbnail if available
                    var img = link.QuerySelector("img");
                    var thumbnail = img?.GetAttribute("src");

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
    /// Decodes Google's redirect-protected URLs (/url?q=...&sa=U...).
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
