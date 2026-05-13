using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Wikipedia.
/// Uses the Wikimedia REST API (rest_v1/page/summary) to retrieve article summaries.
/// Based on SearXNG's <c>wikipedia.py</c> with support for multiple language editions
/// and LanguageConverter variants (e.g., simplified/traditional Chinese).
/// </summary>
public class WikipediaSearchEngine : SearchEngineBase
{
    // Wikipedia API endpoint template: {language}.wikipedia.org
    private const string _restV1SummaryUrl = "https://{0}.wikipedia.org/api/rest_v1/page/summary/{1}";

    // Fallback to English if language is unknown
    private const string _defaultLanguage = "en";

    // Language variants that use LanguageConverter (content auto-conversion based on locale)
    private static readonly HashSet<string> _languageConverterVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh", "zh-cn", "zh-tw", "zh-hk", "zh-sg", "zh-mo",
    };

    /// <inheritdoc />
    public override string Name => "wikipedia";

    /// <inheritdoc />
    public override string DisplayName => "Wikipedia";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.Science };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 5.0;

    /// <summary>
    /// Initializes a new instance of the Wikipedia search engine.
    /// </summary>
    public WikipediaSearchEngine() : base() { }

    /// <summary>
    /// Initializes a new instance with a logger.
    /// </summary>
    public WikipediaSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            // Wikipedia API expects capitalized first letter for most languages
            var searchQuery = query.Query;
            if (!char.IsUpper(searchQuery[0]) && !_languageConverterVariants.Contains(GetLanguageCode(query.Language)))
                searchQuery = char.ToUpperInvariant(searchQuery[0]) + searchQuery[1..];

            var language = GetLanguageCode(query.Language);
            var encodedTitle = Uri.EscapeDataString(searchQuery);
            var url = string.Format(_restV1SummaryUrl, language, encodedTitle);

            using var request = CreateGetRequest(url);

            // For LanguageConverter variants, set Accept-Language for content variant
            if (_languageConverterVariants.Contains(query.Language?.ToLowerInvariant() ?? ""))
            {
                request.Headers.TryAddWithoutValidation("Accept-Language", query.Language);
            }

            // Don't throw on 404 (page not found) — that's normal for Wikipedia
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await SendRequestAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.Debug("{Engine}: Page not found: {Query}", Name, query.Query);
                return new SearchResultList { Results = Array.Empty<SearchResult>() };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // Might be invalid characters in the title
                _logger.Debug("{Engine}: Bad request for: {Query}", Name, query.Query);
                return new SearchResultList { Results = Array.Empty<SearchResult>() };
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseSummaryResponse(json, language);

            return new SearchResultList { Results = results };
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
    /// Parses the Wikipedia REST API summary response.
    /// Based on SearXNG's <c>wikipedia.py response()</c>.
    /// </summary>
    private List<SearchResult> ParseSummaryResponse(string json, string language)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = root.GetProperty("titles").GetProperty("display").GetString()
                     ?? root.GetProperty("title").GetString()
                     ?? "Unknown";

            var pageUrl = root.GetProperty("content_urls").GetProperty("desktop").GetProperty("page").GetString() ?? string.Empty;
            var description = root.GetProperty("description").GetString() ?? string.Empty;
            var extract = root.GetProperty("extract").GetString() ?? string.Empty;

            // Truncate extract to a reasonable length
            if (extract.Length > 500)
                extract = extract[..500] + "...";

            var pageType = root.GetProperty("type").GetString() ?? "standard";

            // Build the result with optional thumbnail
            string? thumbUrl = null;
            if (root.TryGetProperty("thumbnail", out var thumbnail))
                thumbUrl = thumbnail.GetProperty("source").GetString();

            var result = new SearchResult
            {
                Url = pageUrl,
                Title = title,
                Content = extract,
                Engine = Name,
                Type = SearchResultType.Default,
                Category = SearchCategory.Web,
                Metadata = description,
                Thumbnail = thumbUrl,
                Position = 1,
            };

            results.Add(result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse summary JSON", Name);
        }

        return results;
    }

    /// <summary>
    /// Maps SearXNG language codes to Wikipedia subdomains.
    /// </summary>
    private static string GetLanguageCode(string? language)
    {
        if (string.IsNullOrEmpty(language) || language == "auto")
            return _defaultLanguage;

        // Normalize: "en-US" -> "en", "zh-CN" -> "zh"
        var parts = language.Split('-');
        return parts[0].ToLowerInvariant();
    }
}
