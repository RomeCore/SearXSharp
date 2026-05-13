using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Discourse forums.
/// Uses the Discourse search API (JSON).
/// Based on SearXNG's discourse.py.
/// </summary>
public class DiscourseSearchEngine : SearchEngineBase
{
    /// <summary>
    /// Base URL of the Discourse forum (e.g., "https://forums.paddling.com/").
    /// </summary>
    public string BaseUrl { get; set; } = "https://meta.discourse.org";

    private static readonly Dictionary<TimeRange, int> _timeRangeMap = new()
    {
        [TimeRange.Day] = 1,
        [TimeRange.Week] = 7,
        [TimeRange.Month] = 31,
        [TimeRange.Year] = 365,
    };

    /// <inheritdoc />
    public override string Name => "discourse";

    /// <inheritdoc />
    public override string DisplayName => "Discourse";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.SocialMedia, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public DiscourseSearchEngine() : base() { }
    public DiscourseSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            if (query.Query.Length <= 2)
                return CreateResultList(new List<SearchResult>());

            var q = $"{query.Query} order:likes";
            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var days))
            {
                var afterDate = DateTime.UtcNow.AddDays(-days);
                q += $" after:{afterDate:yyyy-MM-dd}";
            }

            var url = $"{BaseUrl.TrimEnd('/')}/search.json?q={Uri.EscapeDataString(q)}&page={query.Page}";

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

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
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            if (!root.TryGetProperty("topics", out var topics) || !root.TryGetProperty("posts", out var posts))
                return CreateResultList(results);

            var topicsDict = new Dictionary<int, JsonElement>();
            foreach (var topic in topics.EnumerateArray())
            {
                if (topic.TryGetProperty("id", out var idEl))
                    topicsDict[idEl.GetInt32()] = topic;
            }

            var baseUrl = BaseUrl.TrimEnd('/');

            foreach (var post in posts.EnumerateArray())
            {
                try
                {
                    var topicId = post.GetProperty("topic_id").GetInt32();
                    if (!topicsDict.TryGetValue(topicId, out var topic))
                        continue;

                    var postId = post.GetProperty("id").GetInt32();
                    var url = $"{baseUrl}/p/{postId}";

                    var title = System.Net.WebUtility.HtmlDecode(
                        topic.GetProperty("title").GetString() ?? "");

                    var blurb = post.TryGetProperty("blurb", out var blurbEl)
                        ? System.Net.WebUtility.HtmlDecode(blurbEl.GetString() ?? "") : "";

                    var username = post.TryGetProperty("username", out var userEl)
                        ? userEl.GetString() ?? "" : "";

                    var postsCount = topic.TryGetProperty("posts_count", out var pc)
                        ? pc.GetInt32() : 0;

                    var hasAccepted = topic.TryGetProperty("has_accepted_answer", out var acc)
                        && acc.GetBoolean();

                    var closed = topic.TryGetProperty("closed", out var cl) && cl.GetBoolean();

                    var isOpen = !closed;

                    DateTime? publishedDate = null;
                    if (topic.TryGetProperty("created_at", out var dtEl))
                    {
                        if (DateTime.TryParse(dtEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    var metadataParts = new List<string>();
                    if (!string.IsNullOrEmpty(username)) metadataParts.Add($"@{username}");
                    if (postsCount > 1) metadataParts.Add($"comments: {postsCount}");
                    if (hasAccepted) metadataParts.Add("answered");
                    else if (postsCount > 1) metadataParts.Add(isOpen ? "open" : "closed");

                    var thumbnail = "";
                    if (post.TryGetProperty("avatar_template", out var avatarEl))
                    {
                        var avatar = (avatarEl.GetString() ?? "").Replace("{size}", "96");
                        if (!string.IsNullOrEmpty(avatar))
                            thumbnail = baseUrl + avatar;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = blurb,
                        PublishedDate = publishedDate,
                        Metadata = string.Join(" | ", metadataParts),
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        Engine = Name,
                        Category = SearchCategory.SocialMedia,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse post", Name);
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
