using AngleSharp;
using AngleSharp.Dom;
using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Yandex (web + images).
/// Uses HTML scraping with CAPTCHA detection.
/// Based on SearXNG's yandex.py.
/// </summary>
public class YandexSearchEngine : SearchEngineBase
{
    private const string _baseUrlWeb = "https://yandex.com/search/site/";
    private const string _baseUrlImages = "https://yandex.com/images/search";

    private static readonly Regex _serpJsonRegex = new(
        @"\{(\\n\s+)?""location"":""/images/search/", RegexOptions.Compiled);

    private static readonly string[] _supportedLangs = { "ru", "en", "be", "fr", "de", "id", "kk", "tt", "tr", "uk" };

    /// <inheritdoc />
    public override string Name => "yandex";

    /// <inheritdoc />
    public override string DisplayName => "Yandex";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Web, SearchCategory.Images };

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

    public YandexSearchEngine() : base() { }
    public YandexSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var cookie = "yp=1716337604.sp.family%3A0%231685406411.szm.1:1920x1080:1920x999";
            var lang = "en";
            if (query.Language != null)
            {
                var langCode = query.Language.Split('-')[0];
                if (_supportedLangs.Contains(langCode))
                    lang = langCode;
            }

            var url = query.Category == SearchCategory.Images
                ? BuildImagesUrl(query, lang)
                : BuildWebUrl(query, lang);

            var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            // Check for CAPTCHA
            if (response.Headers.TryGetValues("x-yandex-captcha", out var captcha)
                && captcha.Any(c => c.Contains("captcha", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Warning("{Engine}: CAPTCHA detected", Name);
                return CreateErrorResult("captcha");
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            return query.Category == SearchCategory.Images
                ? ParseImages(html)
                : ParseWeb(html);
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

    private string BuildWebUrl(SearchQuery query, string lang)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["tmpl_version"] = "releases",
            ["text"] = query.Query,
            ["web"] = "1",
            ["frame"] = "1",
            ["searchid"] = "3131712",
            ["lang"] = lang,
        };

        if (query.Page > 1)
            queryParams["p"] = (query.Page - 1).ToString();

        return _baseUrlWeb + "?" + string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private string BuildImagesUrl(SearchQuery query, string lang)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["text"] = query.Query,
            ["uinfo"] = "sw-1920-sh-1080-ww-1125-wh-999",
        };

        if (query.Page > 1)
            queryParams["p"] = (query.Page - 1).ToString();

        return _baseUrlImages + "?" + string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private SearchResultList ParseWeb(string html)
    {
        var results = new List<SearchResult>();
        var context = BrowsingContext.New(Configuration.Default);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var items = doc.QuerySelectorAll("li.serp-item");
        foreach (var item in items)
        {
            try
            {
                var link = item.QuerySelector("a.b-serp-item__title-link");
                if (link == null) continue;

                var url = link.GetAttribute("href") ?? "";
                var titleSpan = item.QuerySelector("h3.b-serp-item__title span");
                var title = titleSpan?.TextContent?.Trim()
                    ?? item.QuerySelector("h3.b-serp-item__title")?.TextContent?.Trim() ?? "";

                var contentDiv = item.QuerySelector("div.b-serp-item__text");
                var content = contentDiv?.TextContent?.Trim() ?? "";

                results.Add(new SearchResult
                {
                    Url = url,
                    Title = title,
                    Content = content,
                    Engine = Name,
                    Category = SearchCategory.Web,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "{Engine}: Failed to parse web result", Name);
            }
        }

        return CreateResultList(results);
    }

    private SearchResultList ParseImages(string html)
    {
        var results = new List<SearchResult>();

        // Extract JSON from the page
        var jsonStart = html.IndexOf("{\"location\":\"/images/search/", StringComparison.Ordinal);
        if (jsonStart < 0)
        {
            // Try alternative pattern
            jsonStart = html.IndexOf("{\"location\":\"/images/search/", StringComparison.Ordinal);
            if (jsonStart < 0) return CreateResultList(results);
        }

        // Find the end of the JSON structure
        var jsonEnd = html.IndexOf("advRsyaSearchColumn\":null}}", jsonStart, StringComparison.Ordinal);
        if (jsonEnd < 0)
        {
            jsonEnd = html.IndexOf("false}}}", jsonStart, StringComparison.Ordinal);
            if (jsonEnd < 0) return CreateResultList(results);
            jsonEnd += "false}}}".Length;
        }
        else
        {
            jsonEnd += "advRsyaSearchColumn\":null}}".Length;
        }

        var jsonText = html[jsonStart..jsonEnd];

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var initialState = root.GetProperty("initialState");
            var serpList = initialState.GetProperty("serpList");
            var items = serpList.GetProperty("items");
            var entities = items.GetProperty("entities");

            foreach (var entity in entities.EnumerateObject())
            {
                try
                {
                    var entityData = entity.Value;
                    var snippet = entityData.GetProperty("snippet");
                    var title = snippet.GetProperty("title").GetString() ?? "";
                    var source = snippet.GetProperty("url").GetString() ?? "";
                    var thumb = entityData.GetProperty("image").GetString() ?? "";

                    var viewerData = entityData.GetProperty("viewerData");
                    var dups = viewerData.GetProperty("dups")[0];
                    var fullsizeImage = dups.GetProperty("url").GetString() ?? "";
                    var height = dups.GetProperty("h").GetInt32();
                    var width = dups.GetProperty("w").GetInt32();

                    results.Add(new SearchResult
                    {
                        Url = source,
                        Title = title,
                        ImgSrc = fullsizeImage,
                        Thumbnail = thumb,
                        Resolution = $"{width} x {height}",
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse image entity", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse images JSON", Name);
        }

        return CreateResultList(results);
    }
}
