using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Brave Search (search.brave.com).
/// Uses HTML scraping to retrieve search results across multiple categories (web, news, images, videos).
/// Based on SearXNG's <c>brave.py</c> with AngleSharp for HTML parsing.
/// </summary>
public class BraveSearchEngine : SearchEngineBase
{
    // Brave search base URL per category
    private const string _baseUrl = "https://search.brave.com/";

    // Safe search cookie values
    private static readonly Dictionary<SafeSearchLevel, string> _safeSearchMap = new()
    {
        [SafeSearchLevel.None] = "off",
        [SafeSearchLevel.Moderate] = "moderate",
        [SafeSearchLevel.Strict] = "strict",
    };

    /// <inheritdoc />
    public override string Name => "brave";

    /// <inheritdoc />
    public override string DisplayName => "Brave";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.News, SearchCategory.Images, SearchCategory.Videos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    /// <summary>
    /// Initializes a new instance of the Brave search engine.
    /// </summary>
    public BraveSearchEngine() : base() { }

    /// <summary>
    /// Initializes a new instance with a logger.
    /// </summary>
    public BraveSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            return query.Category switch
            {
                SearchCategory.Images => await SearchCategoryAsync(query, "images", ct),
                SearchCategory.Videos => await SearchCategoryAsync(query, "videos", ct),
                SearchCategory.News => await SearchCategoryAsync(query, "news", ct),
                _ => await SearchWebAsync(query, ct),
            };
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
    /// Performs a general web search on Brave.
    /// </summary>
    private async Task<SearchResultList> SearchWebAsync(SearchQuery query, CancellationToken ct)
    {
        var args = new Dictionary<string, string>
        {
            ["q"] = query.Query,
            ["source"] = "web",
        };

        if (query.Page > 1)
            args["offset"] = (query.Page - 1).ToString();

        // Build URL with query params (minus cookies)
        var url = _baseUrl + "search?" + string.Join("&", args.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        using var request = CreateGetRequest(url);

        // Brave uses cookies for safesearch, country, and UI language
        request.Headers.Add("Cookie",
            $"safesearch={_safeSearchMap[query.SafeSearch]}; " +
            $"useLocation=0; " +
            $"summarizer=0;");

        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var results = ParseSearchResults(html);

        _logger.Debug("{Engine}: Parsed {Count} web results", Name, results.Count);
        return CreateResultList(results);
    }

    /// <summary>
    /// Performs a category-specific search (images, videos, news) on Brave.
    /// </summary>
    private async Task<SearchResultList> SearchCategoryAsync(SearchQuery query, string category, CancellationToken ct)
    {
        var args = new Dictionary<string, string>
        {
            ["q"] = query.Query,
        };

        var url = _baseUrl + category + "?" + string.Join("&", args.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        using var request = CreateGetRequest(url);
        request.Headers.Add("Cookie",
            $"safesearch={_safeSearchMap[query.SafeSearch]}; " +
            $"useLocation=0;");

        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);

        // Category pages use embedded JSON data in a <script> tag
        var results = category switch
        {
            "images" => ParseImageResults(html),
            "videos" => ParseVideoResults(html),
            "news" => ParseNewsResults(html),
            _ => ParseSearchResults(html),
        };

        _logger.Debug("{Engine}: Parsed {Count} {Category} results", Name, results.Count, category);
        return CreateResultList(results);
    }

    /// <summary>
    /// Parses Brave web search results from HTML.
    /// Based on SearXNG's <c>brave.py _parse_search()</c>.
    /// </summary>
    private List<SearchResult> ParseSearchResults(string html)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(html)) return results;

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var snippets = document.QuerySelectorAll("div.snippet");
            var position = (1 - 1) * 10 + 1;

            foreach (var snippet in snippets)
            {
                try
                {
                    var link = snippet.QuerySelector("a");
                    var href = link?.GetAttribute("href");
                    if (string.IsNullOrEmpty(href)) continue;

                    var titleElement = snippet.QuerySelector("div.title");
                    var title = titleElement?.TextContent.Trim();
                    if (string.IsNullOrEmpty(title)) continue;

                    // Content is in the second div with class "content"
                    var contentElement = snippet.QuerySelector("div.content");
                    var content = contentElement?.TextContent.Trim() ?? string.Empty;

                    // Remove the date prefix from content if present
                    var dateSpan = contentElement?.QuerySelector("span.t-secondary");
                    var dateText = dateSpan?.TextContent.Trim();
                    if (!string.IsNullOrEmpty(dateText) && content.StartsWith(dateText))
                        content = content[dateText.Length..].Trim('-', ' ', '\n', '\t').Trim();

                    DateTime? publishedDate = null;
                    if (!string.IsNullOrEmpty(dateText))
                        publishedDate = TryParseDate(dateText);

                    var thumbnail = snippet.QuerySelector("a.thumbnail img")?.GetAttribute("src");

                    results.Add(new SearchResult
                    {
                        Url = href,
                        Title = title,
                        Content = content,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Web,
                        Thumbnail = thumbnail,
                        PublishedDate = publishedDate,
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse a snippet", Name);
                }
            }

            // Extract suggestions
            var suggestions = document.QuerySelectorAll("a.related-query");
            // Suggestions could be added to result list if needed
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse search HTML", Name);
        }

        return results;
    }

    /// <summary>
    /// Parses Brave image search results from embedded JSON in HTML.
    /// Based on SearXNG's <c>brave.py _parse_images()</c>.
    /// </summary>
    private List<SearchResult> ParseImageResults(string html)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(html)) return results;

        try
        {
            // Try to extract JSON data from <script> tags
            // Format: <script>kit.start(app, element, {data: [{type:"data",data: ....}]})</script>
            var jsonData = ExtractJsonFromScript(html);
            if (jsonData == null) return results;
            var jd = jsonData.Value;

            // Navigate to data[1].data.body.response.results
            var responseNode = jd.GetProperty("data")[1]
                .GetProperty("data")
                .GetProperty("body")
                .GetProperty("response");

            var items = responseNode.GetProperty("results");
            var position = 1;

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var url = item.GetProperty("url").GetString() ?? string.Empty;
                    var title = item.GetProperty("title").GetString() ?? string.Empty;
                    var source = item.GetProperty("source").GetString() ?? string.Empty;
                    var imgSrc = item.GetProperty("properties").GetProperty("url").GetString() ?? string.Empty;
                    var thumbSrc = item.GetProperty("thumbnail").GetProperty("src").GetString() ?? string.Empty;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = source,
                        Engine = Name,
                        Type = SearchResultType.Image,
                        Category = SearchCategory.Images,
                        ImgSrc = imgSrc,
                        Thumbnail = thumbSrc,
                        Source = source,
                        Position = position++,
                    });
                }
                catch { /* skip malformed items */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Engine}: Failed to parse image JSON (expected for non-image pages)", Name);
        }

        return results;
    }

    /// <summary>
    /// Parses Brave video search results.
    /// </summary>
    private List<SearchResult> ParseVideoResults(string html)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(html)) return results;

        try
        {
            var jsonData = ExtractJsonFromScript(html);
            if (jsonData == null) return results;
            var jd = jsonData.Value;

            var responseNode = jd.GetProperty("data")[1]
                .GetProperty("data")
                .GetProperty("body")
                .GetProperty("response");

            var items = responseNode.GetProperty("results");
            var position = 1;

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var url = item.GetProperty("url").GetString() ?? string.Empty;
                    var title = item.GetProperty("title").GetString() ?? string.Empty;
                    var description = item.GetProperty("description").GetString() ?? string.Empty;
                    var duration = item.GetProperty("video").GetProperty("duration").GetString() ?? string.Empty;

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("age", out var ageProp))
                        publishedDate = TryParseDate(ageProp.GetString() ?? string.Empty);

                    string? thumbnail = null;
                    if (item.TryGetProperty("thumbnail", out var thumbProp))
                        thumbnail = thumbProp.GetProperty("src").GetString();

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Engine = Name,
                        Type = SearchResultType.Video,
                        Category = SearchCategory.Videos,
                        Thumbnail = thumbnail,
                        PublishedDate = publishedDate,
                        Duration = TryParseDuration(duration),
                        Position = position++,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Engine}: Failed to parse video JSON", Name);
        }

        return results;
    }

    /// <summary>
    /// Parses Brave news search results from HTML.
    /// Based on SearXNG's <c>brave.py _parse_news()</c>.
    /// </summary>
    private List<SearchResult> ParseNewsResults(string html)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(html)) return results;

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var newsItems = document.QuerySelectorAll("div[data-type='news']");
            var position = 1;

            foreach (var item in newsItems)
            {
                try
                {
                    var link = item.QuerySelector("a.result-header");
                    var url = link?.GetAttribute("href");
                    if (string.IsNullOrEmpty(url)) continue;

                    var titleElement = item.QuerySelector("span.snippet-title");
                    var title = titleElement?.TextContent.Trim();
                    if (string.IsNullOrEmpty(title)) continue;

                    var contentElement = item.QuerySelector("p.desc");
                    var content = contentElement?.TextContent.Trim() ?? string.Empty;

                    var thumbnail = item.QuerySelector("div.image-wrapper img")?.GetAttribute("src");

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Engine = Name,
                        Type = SearchResultType.News,
                        Category = SearchCategory.News,
                        Thumbnail = thumbnail,
                        Position = position++,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse news HTML", Name);
        }

        return results;
    }

    /// <summary>
    /// Extracts embedded JSON from Brave's <script> tags.
    /// Based on SearXNG's <c>brave.py extract_json_data()</c>.
    /// </summary>
    private static System.Text.Json.JsonElement? ExtractJsonFromScript(string html)
    {
        const string dataPrefix = "data: [{";
        var scriptStart = html.IndexOf("<script", StringComparison.Ordinal);
        if (scriptStart < 0) return null;

        var dataStart = html.IndexOf(dataPrefix, scriptStart, StringComparison.Ordinal);
        if (dataStart < 0) return null;

        // Find the end of the data structure - look for "}}]"
        var dataEnd = html.LastIndexOf("}}]", dataStart + 5000, StringComparison.Ordinal);
        if (dataEnd < 0) return null;

        var jsonStr = "{" + html[(dataStart + dataPrefix.Length - 1)..(dataEnd + 3)] + "}";

        try
        {
            return System.Text.Json.JsonDocument.Parse(jsonStr).RootElement;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse a date string.
    /// </summary>
    private static DateTime? TryParseDate(string dateStr)
    {
        if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    /// <summary>
    /// Attempts to parse a duration string (e.g., "5:30") into a TimeSpan.
    /// </summary>
    private static TimeSpan? TryParseDuration(string duration)
    {
        var parts = duration.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var sec))
            return new TimeSpan(0, min, sec);
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s))
            return new TimeSpan(h, m, s);
        return null;
    }
}
