using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for GitLab (gitlab.com).
/// Uses official GitLab REST API (no API key required for public repos).
/// Based on SearXNG's gitlab.py.
/// </summary>
public class GitLabSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://gitlab.com";
    private const string _apiPath = "api/v4/projects";

    /// <inheritdoc />
    public override string Name => "gitlab";

    /// <inheritdoc />
    public override string DisplayName => "GitLab";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Repos };

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

    public GitLabSearchEngine() : base() { }
    public GitLabSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["search"] = query.Query,
                ["page"] = query.Page.ToString(),
            };

            var url = $"{_baseUrl}/{_apiPath}?" + string.Join("&", args.Select(kv =>
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

            foreach (var item in root.EnumerateArray())
            {
                try
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    var webUrl = "";
                    if (item.TryGetProperty("web_url", out var wu))
                        webUrl = wu.GetString() ?? "";

                    var description = "";
                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        description = desc.GetString() ?? "";

                    var avatarUrl = "";
                    if (item.TryGetProperty("avatar_url", out var av) && av.ValueKind == JsonValueKind.String)
                        avatarUrl = av.GetString() ?? "";

                    var namespaceName = "";
                    if (item.TryGetProperty("namespace", out var ns)
                        && ns.TryGetProperty("name", out var nsName))
                        namespaceName = nsName.GetString() ?? "";

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("last_activity_at", out var lastActivity))
                    {
                        if (DateTime.TryParse(lastActivity.GetString(), out var dt))
                            publishedDate = dt;
                    }
                    if (publishedDate == null && item.TryGetProperty("created_at", out var created))
                    {
                        if (DateTime.TryParse(created.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    var tagList = new List<string>();
                    if (item.TryGetProperty("tag_list", out var tags))
                    {
                        foreach (var tag in tags.EnumerateArray())
                        {
                            var tagStr = tag.GetString();
                            if (!string.IsNullOrEmpty(tagStr))
                                tagList.Add(tagStr);
                        }
                    }

                    var starCount = 0;
                    if (item.TryGetProperty("star_count", out var stars))
                        starCount = stars.GetInt32();

                    results.Add(new SearchResult
                    {
                        Url = webUrl,
                        Title = name,
                        Content = description,
                        Thumbnail = string.IsNullOrEmpty(avatarUrl) ? null : avatarUrl,
                        Author = namespaceName,
                        PublishedDate = publishedDate,
                        Tags = tagList,
                        Score = starCount,
                        Engine = Name,
                        Category = SearchCategory.Repos,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse project", Name);
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
