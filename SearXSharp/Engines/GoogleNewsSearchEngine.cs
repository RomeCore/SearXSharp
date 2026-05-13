using AngleSharp;
using SearXSharp.Models;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Google News (news.google.com).
/// Based on SearXNG's google_news.py with CEID handling and consent bypass.
/// </summary>
public class GoogleNewsSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://news.google.com/search";

    /// <inheritdoc />
    public override string Name => "google news";

    /// <inheritdoc />
    public override string DisplayName => "Google News";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.News };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    /// <summary>
    /// CEID (Country/Language) mappings for Google News.
    /// </summary>
    private static readonly Dictionary<string, string> _ceidMap = new()
    {
        ["en-US"] = "US:en", ["ru-RU"] = "RU:ru", ["de-DE"] = "DE:de",
        ["fr-FR"] = "FR:fr", ["es-ES"] = "ES:es", ["it-IT"] = "IT:it",
        ["pt-BR"] = "BR:pt-419", ["ja-JP"] = "JP:ja", ["zh-CN"] = "CN:zh-Hans",
        ["ar-SA"] = "SA:ar", ["ko-KR"] = "KR:ko", ["nl-NL"] = "NL:nl",
        ["pl-PL"] = "PL:pl", ["sv-SE"] = "SE:sv", ["tr-TR"] = "TR:tr",
        ["uk-UA"] = "UA:uk", ["en-GB"] = "GB:en", ["en-CA"] = "CA:en",
        ["en-AU"] = "AU:en", ["en-IN"] = "IN:en", ["hi-IN"] = "IN:hi",
        ["bn-BD"] = "BD:bn", ["th-TH"] = "TH:th", ["vi-VN"] = "VN:vi",
        ["el-GR"] = "GR:el", ["cs-CZ"] = "CZ:cs", ["ro-RO"] = "RO:ro",
        ["hu-HU"] = "HU:hu", ["he-IL"] = "IL:he", ["ar-AE"] = "AE:ar",
        ["zh-TW"] = "TW:zh-Hant", ["zh-HK"] = "HK:zh-Hant",
    };

    public GoogleNewsSearchEngine() : base() { }
    public GoogleNewsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var ceid = "US:en";
            if (query.Language != null && _ceidMap.TryGetValue(query.Language, out var mappedCeid))
                ceid = mappedCeid;
            else if (query.Language != null)
            {
                var parts = query.Language.Split('-');
                var langLower = parts[0].ToLowerInvariant();
                foreach (var kv in _ceidMap)
                {
                    if (kv.Value.StartsWith(langLower, StringComparison.OrdinalIgnoreCase)
                        || kv.Key.StartsWith(langLower, StringComparison.OrdinalIgnoreCase))
                    {
                        ceid = kv.Value;
                        break;
                    }
                }
            }

            var ceidParts = ceid.Split(':');
            var hl = ceidParts.Length > 1 ? ceidParts[1] : "en";
            var gl = ceidParts[0];

            var queryParams = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["hl"] = hl,
                ["gl"] = gl,
                ["ceid"] = ceid,
            };

            var url = _baseUrl + "?" + string.Join("&", queryParams.Select(kv =>
                $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));

            var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseHtml(html);
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

    private SearchResultList ParseHtml(string html)
    {
        var results = new List<SearchResult>();
        var context = BrowsingContext.New(Configuration.Default);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var articleDivs = doc.QuerySelectorAll("div.xrnccd");
        foreach (var article in articleDivs)
        {
            try
            {
                var articleTag = article.QuerySelector("article");
                if (articleTag == null) continue;

                var link = articleTag.QuerySelector("a");
                if (link == null) continue;

                var href = link.GetAttribute("href") ?? "";
                // Decode Google News internal URL (base64-encoded)
                var decodedUrl = DecodeGoogleNewsUrl(href);

                var h3 = articleTag.QuerySelector("h3");
                var title = h3?.TextContent?.Trim() ?? "";

                var timeElement = articleTag.QuerySelector("time");
                var pubDate = timeElement?.TextContent?.Trim() ?? "";

                var sourceElement = articleTag.QuerySelector("a[data-n-tid]");
                var pubOrigin = sourceElement?.TextContent?.Trim() ?? "";

                var content = string.Join(" / ", new[] { pubOrigin, pubDate }.Where(x => !string.IsNullOrEmpty(x)));

                // Thumbnail from preceding sibling <a><figure><img>
                var figureImg = article.PreviousElementSibling?.QuerySelector("figure img");
                var thumbnail = figureImg?.GetAttribute("src") ?? "";

                results.Add(new SearchResult
                {
                    Url = decodedUrl,
                    Title = title,
                    Content = content,
                    Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                    Engine = Name,
                    Category = SearchCategory.News,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "{Engine}: Failed to parse news item", Name);
            }
        }

        return CreateResultList(results);
    }

    private static string DecodeGoogleNewsUrl(string href)
    {
        try
        {
            // Google News URLs look like: ./read/ABC123... where ABC123 is base64
            var parts = href.Split('?');
            var lastPart = parts[0].Split('/').LastOrDefault();
            if (lastPart == null) return href;

            // Add padding
            var padded = lastPart;
            while (padded.Length % 4 != 0) padded += "=";

            var bytes = Convert.FromBase64String(padded);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);

            // Extract URL starting with http
            var httpIndex = decoded.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (httpIndex >= 0)
            {
                var urlPart = decoded[httpIndex..];
                var endIndex = urlPart.IndexOf('\u00d2');
                if (endIndex > 0) urlPart = urlPart[..endIndex];
                return urlPart;
            }

            return href;
        }
        catch
        {
            return href;
        }
    }
}
