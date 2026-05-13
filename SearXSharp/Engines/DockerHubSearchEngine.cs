using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Docker Hub (hub.docker.com).
/// Uses the official Docker Hub API v3 to search for container images.
/// Based on SearXNG's docker_hub.py.
/// </summary>
public class DockerHubSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://hub.docker.com";
    private const string _searchApi = "https://hub.docker.com/api/search/v3/catalog/search";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "dockerhub";

    /// <inheritdoc />
    public override string DisplayName => "Docker Hub";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Packages };

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

    public DockerHubSearchEngine() : base() { }
    public DockerHubSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["query"] = query.Query,
                ["from"] = (_pageSize * (query.Page - 1)).ToString(),
                ["size"] = _pageSize.ToString(),
            };

            var url = _searchApi + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.UserAgent.TryParseAdd(GetRandomUserAgent());

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} Docker Hub results", Name, results.Count);
            return CreateResultList(results);
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

    private List<SearchResult> ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var resultsArray))
                return results;

            foreach (var item in resultsArray.EnumerateArray())
            {
                try
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    var slug = item.GetProperty("slug").GetString() ?? "";
                    var shortDescription = item.GetProperty("short_description").GetString() ?? "";
                    var source = item.GetProperty("source").GetString() ?? "";
                    var isOfficial = source is "store" or "official";

                    var url = _baseUrl + (isOfficial ? "/_/" : "/r/") + slug;

                    // Logo/thumbnail
                    string? thumbnail = null;
                    if (item.TryGetProperty("logo_url", out var logoUrl))
                    {
                        if (logoUrl.TryGetProperty("large", out var large))
                            thumbnail = large.GetString();
                        else if (logoUrl.TryGetProperty("small", out var small))
                            thumbnail = small.GetString();
                    }

                    // Publisher
                    var publisher = "";
                    if (item.TryGetProperty("publisher", out var publisherEl))
                        publisher = publisherEl.GetProperty("name").GetString() ?? "";

                    // Star count
                    var starCount = 0;
                    if (item.TryGetProperty("star_count", out var stars))
                        starCount = stars.GetInt32();

                    // Pull count & architectures
                    var popularityParts = new List<string> { $"{starCount} stars" };
                    var architectures = new List<string>();

                    if (item.TryGetProperty("rate_plans", out var ratePlans))
                    {
                        foreach (var plan in ratePlans.EnumerateArray())
                        {
                            var repos = plan.GetProperty("repositories");
                            if (repos.GetArrayLength() > 0)
                            {
                                var repo = repos[0];
                                if (repo.TryGetProperty("pull_count", out var pulls))
                                    popularityParts.Insert(0, $"{pulls.GetInt64():N0} pulls");
                            }

                            if (plan.TryGetProperty("architectures", out var archs))
                            {
                                foreach (var arch in archs.EnumerateArray())
                                {
                                    var archName = arch.GetProperty("name").GetString();
                                    if (!string.IsNullOrEmpty(archName))
                                        architectures.Add(archName);
                                }
                            }
                        }
                    }

                    // Updated date
                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("updated_at", out var updated))
                    {
                        if (DateTime.TryParse(updated.GetString(), out var dt))
                            publishedDate = dt;
                    }
                    else if (item.TryGetProperty("created_at", out var created))
                    {
                        if (DateTime.TryParse(created.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    var content = shortDescription;
                    var popularity = string.Join(", ", popularityParts);

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = name,
                        Content = string.IsNullOrEmpty(content) ? popularity : $"{content} | {popularity}",
                        Thumbnail = thumbnail,
                        Author = publisher,
                        PublishedDate = publishedDate,
                        Tags = architectures,
                        Metadata = popularity,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse Docker Hub result item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return results;
    }
}
