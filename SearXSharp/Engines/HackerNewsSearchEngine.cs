using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Hacker News (news.ycombinator.com).
/// Uses Algolia API (hn.algolia.com) — no API key required.
/// Based on SearXNG's hackernews.py.
/// </summary>
public class HackerNewsSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://hn.algolia.com/api/v1";

    /// <inheritdoc />
    public override string Name => "hackernews";

    /// <inheritdoc />
    public override string DisplayName => "Hacker News";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.News };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public HackerNewsSearchEngine() : base() { }
    public HackerNewsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var searchType = "search";
            var queryParams = new Dictionary<string, string>
            {
                ["query"] = query.Query,
                ["page"] = (query.Page - 1).ToString(),
                ["hitsPerPage"] = "30",
                ["minWordSizefor1Typo"] = "4",
                ["minWordSizefor2Typos"] = "8",
                ["advancedSyntax"] = "true",
                ["ignorePlurals"] = "false",
                ["minProximity"] = "7",
                ["numericFilters"] = "[]",
                ["tagFilters"] = "[\"story\",[]]",
                ["typoTolerance"] = "true",
                ["queryType"] = "prefixLast",
                ["restrictSearchableAttributes"] = "[\"title\",\"comment_text\",\"url\",\"story_text\",\"author\"]",
                ["getRankingInfo"] = "true",
            };

            if (query.TimeRange.HasValue)
            {
                searchType = "search_by_date";
                var seconds = query.TimeRange.Value switch
                {
                    TimeRange.Day => 86400,
                    TimeRange.Week => 604800,
                    TimeRange.Month => 2592000,
                    TimeRange.Year => 31536000,
                    _ => 0,
                };
                if (seconds > 0)
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
                    queryParams["numericFilters"] = $"created_at_i>{timestamp}";
                }
            }

            var url = $"{_baseUrl}/{searchType}?" + string.Join("&", queryParams.Select(kv =>
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
            _logger.Error(ex, "{Engine}: Search failed for query: {Query}", Name, query.Query);
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

            if (!root.TryGetProperty("hits", out var hits))
                return CreateResultList(results);

            foreach (var hit in hits.EnumerateArray())
            {
                try
                {
                    var objectId = hit.GetProperty("objectID").GetString() ?? "";

                    var points = 0;
                    if (hit.TryGetProperty("points", out var pointsEl))
                        points = pointsEl.GetInt32();

                    var numComments = 0;
                    if (hit.TryGetProperty("num_comments", out var commentsEl))
                        numComments = commentsEl.GetInt32();

                    var title = "";
                    if (hit.TryGetProperty("title", out var titleEl))
                        title = titleEl.GetString() ?? "";

                    var author = "";
                    if (hit.TryGetProperty("author", out var authorEl))
                        author = authorEl.GetString() ?? "";

                    // Build content from url, comment_text, or story_text
                    var content = "";
                    if (hit.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                        content = urlEl.GetString() ?? "";
                    if (string.IsNullOrEmpty(content) && hit.TryGetProperty("comment_text", out var commentEl))
                        content = StripHtml(commentEl.GetString() ?? "");
                    if (string.IsNullOrEmpty(content) && hit.TryGetProperty("story_text", out var storyEl))
                        content = StripHtml(storyEl.GetString() ?? "");

                    // Fallback title
                    if (string.IsNullOrEmpty(title))
                        title = $"author: {author}";

                    // Metadata
                    var metadata = "";
                    if (points != 0 || numComments != 0)
                        metadata = $"points: {points} | comments: {numComments}";

                    // Published date
                    DateTime? publishedDate = null;
                    if (hit.TryGetProperty("created_at_i", out var createdEl))
                    {
                        var unixTime = createdEl.GetInt64();
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }

                    results.Add(new SearchResult
                    {
                        Url = $"https://news.ycombinator.com/item?id={objectId}",
                        Title = title,
                        Content = Truncate(content, 500),
                        Author = author,
                        PublishedDate = publishedDate,
                        Metadata = metadata,
                        Engine = Name,
                        Category = SearchCategory.IT,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse hit", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ").Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
