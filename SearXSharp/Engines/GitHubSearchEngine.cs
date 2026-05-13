using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for GitHub repository search.
/// Uses the official GitHub API (no API key required for public repos).
/// Based on SearXNG's github.py.
/// </summary>
public class GitHubSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.github.com/search/repositories?sort=stars&order=desc&q=";

    /// <inheritdoc />
    public override string Name => "github";

    /// <inheritdoc />
    public override string DisplayName => "GitHub";

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

    /// <summary>
    /// Initializes a new instance of GitHub search engine.
    /// Overrides the default user-agent with GitHub API requirements.
    /// </summary>
    public GitHubSearchEngine() : base() { }
    public GitHubSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _searchUrl + Uri.EscapeDataString(query.Query);

            var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Accept",
                "application/vnd.github.preview.text-match+json");

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

            if (!root.TryGetProperty("items", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var fullName = item.GetProperty("full_name").GetString() ?? "";
                    var htmlUrl = item.GetProperty("html_url").GetString() ?? "";
                    var description = "";
                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        description = desc.GetString() ?? "";

                    var language = "";
                    if (item.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
                        language = lang.GetString() ?? "";

                    var contentParts = new List<string>();
                    if (!string.IsNullOrEmpty(language)) contentParts.Add(language);
                    if (!string.IsNullOrEmpty(description)) contentParts.Add(description);
                    var content = string.Join(" / ", contentParts);

                    var avatarUrl = "";
                    if (item.TryGetProperty("owner", out var owner)
                        && owner.TryGetProperty("avatar_url", out var avatar))
                        avatarUrl = avatar.GetString() ?? "";

                    var name = item.GetProperty("name").GetString() ?? "";

                    var login = "";
                    if (item.TryGetProperty("owner", out var owner2)
                        && owner2.TryGetProperty("login", out var log))
                        login = log.GetString() ?? "";

                    var updatedAt = item.GetProperty("updated_at").GetString() ?? "";
                    var createdAt = item.GetProperty("created_at").GetString() ?? "";

                    DateTime? publishedDate = null;
                    if (DateTime.TryParse(updatedAt, out var updated))
                        publishedDate = updated;
                    else if (DateTime.TryParse(createdAt, out var created))
                        publishedDate = created;

                    var topics = new List<string>();
                    if (item.TryGetProperty("topics", out var topicsEl))
                        topics = topicsEl.EnumerateArray().Select(t => t.GetString() ?? "").ToList();

                    var stars = 0;
                    if (item.TryGetProperty("stargazers_count", out var starsEl))
                        stars = starsEl.GetInt32();

                    string? licenseName = null;
                    string? licenseUrl = null;
                    if (item.TryGetProperty("license", out var license) && license.ValueKind == JsonValueKind.Object)
                    {
                        if (license.TryGetProperty("spdx_id", out var spdx) && spdx.GetString() is { } spdxStr && !string.IsNullOrEmpty(spdxStr))
                        {
                            licenseName = spdxStr;
                            licenseUrl = $"https://spdx.org/licenses/{spdxStr}.html";
                        }
                        if (license.TryGetProperty("name", out var licName))
                            licenseName = licName.GetString() ?? licenseName;
                    }

                    var homepage = "";
                    if (item.TryGetProperty("homepage", out var home) && home.ValueKind == JsonValueKind.String)
                        homepage = home.GetString() ?? "";

                    var cloneUrl = "";
                    if (item.TryGetProperty("clone_url", out var clone) && clone.ValueKind == JsonValueKind.String)
                        cloneUrl = clone.GetString() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = htmlUrl,
                        Title = fullName,
                        Content = content,
                        Thumbnail = avatarUrl,
                        Author = login,
                        PublishedDate = publishedDate,
                        Tags = topics,
                        Score = stars,
                        Source = homepage,
                        Engine = Name,
                        Category = SearchCategory.Repos,
                        Type = SearchResultType.Default,
                        Metadata = licenseName,
                        // Custom extras via metadata-like fields
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse repository item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON response", Name);
        }

        return CreateResultList(results);
    }
}
