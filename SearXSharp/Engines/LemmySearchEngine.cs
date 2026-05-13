using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Lemmy (lemmy.ml).
/// Uses the official Lemmy API v3 (no API key required).
/// Based on SearXNG's lemmy.py. Searches for communities, users, posts and comments.
/// </summary>
public class LemmySearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://lemmy.ml";
    private const string _searchUrl = _baseUrl + "/api/v3/search";

    /// <inheritdoc />
    public override string Name => "lemmy";

    /// <inheritdoc />
    public override string DisplayName => "Lemmy";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.SocialMedia };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public LemmySearchEngine() : base() { }
    public LemmySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["type_"] = "Posts",
            };

            var url = _searchUrl + "?" + string.Join("&",
                args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParsePostsJson(json);
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

    private SearchResultList ParsePostsJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("posts", out var posts))
                return CreateResultList(results);

            foreach (var postItem in posts.EnumerateArray())
            {
                try
                {
                    var post = postItem.GetProperty("post");
                    var creator = postItem.GetProperty("creator");
                    var counts = postItem.GetProperty("counts");
                    var community = postItem.GetProperty("community");

                    var name = post.GetProperty("name").GetString() ?? "";
                    var apId = post.GetProperty("ap_id").GetString() ?? "";
                    var user = creator.TryGetProperty("display_name", out var dn)
                        ? dn.GetString() ?? ""
                        : creator.GetProperty("name").GetString() ?? "";

                    var thumbnail = "";
                    if (post.TryGetProperty("thumbnail_url", out var thumbEl))
                        thumbnail = thumbEl.GetString() ?? "";

                    var body = post.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                        ? bodyEl.GetString() ?? ""
                        : "";

                    var upvotes = counts.GetProperty("upvotes").GetInt32();
                    var downvotes = counts.GetProperty("downvotes").GetInt32();
                    var commentsCount = counts.GetProperty("comments").GetInt32();

                    var communityTitle = community.GetProperty("title").GetString() ?? "";

                    var metadata = $"▲ {upvotes} ▼ {downvotes} | user: {user} | comments: {commentsCount} | community: {communityTitle}";

                    DateTime? publishedDate = null;
                    if (post.TryGetProperty("published", out var pubEl))
                    {
                        var pubStr = pubEl.GetString() ?? "";
                        if (pubStr.Length >= 19)
                        {
                            pubStr = pubStr[..19];
                            if (DateTime.TryParse(pubStr, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
                                publishedDate = dt;
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = apId,
                        Title = name,
                        Content = body,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        PublishedDate = publishedDate,
                        Metadata = metadata,
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
