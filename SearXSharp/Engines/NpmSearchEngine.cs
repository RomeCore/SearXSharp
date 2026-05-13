using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for NPM packages (npms.io).
/// Uses npms.io v2 API (no API key required).
/// Based on SearXNG's npm.py.
/// </summary>
public class NpmSearchEngine : SearchEngineBase
{
    private const string _searchApi = "https://api.npms.io/v2/search?";
    private const int _pageSize = 25;

    /// <inheritdoc />
    public override string Name => "npm";

    /// <inheritdoc />
    public override string DisplayName => "NPM";

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

    public NpmSearchEngine() : base() { }
    public NpmSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["from"] = ((query.Page - 1) * _pageSize).ToString(),
                ["q"] = query.Query,
                ["size"] = _pageSize.ToString(),
            };

            var url = _searchApi + string.Join("&", args.Select(kv =>
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

            if (!root.TryGetProperty("results", out var items))
                return CreateResultList(results);

            foreach (var entry in items.EnumerateArray())
            {
                try
                {
                    if (!entry.TryGetProperty("package", out var package))
                        continue;

                    var name = package.GetProperty("name").GetString() ?? "";

                    var description = "";
                    if (package.TryGetProperty("description", out var desc))
                        description = desc.GetString() ?? "";

                    var version = "";
                    if (package.TryGetProperty("version", out var ver))
                        version = ver.GetString() ?? "";

                    var links = package.GetProperty("links");
                    var npmUrl = links.GetProperty("npm").GetString() ?? "";

                    var homepage = "";
                    if (links.TryGetProperty("homepage", out var home))
                        homepage = home.GetString() ?? "";

                    var repository = "";
                    if (links.TryGetProperty("repository", out var repo))
                        repository = repo.GetString() ?? "";

                    var author = "";
                    if (package.TryGetProperty("author", out var authorEl)
                        && authorEl.TryGetProperty("name", out var authorName))
                        author = authorName.GetString() ?? "";

                    DateTime? publishedDate = null;
                    if (package.TryGetProperty("date", out var dateStr))
                    {
                        if (DateTime.TryParse(dateStr.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    // Tags from flags and keywords
                    var tags = new List<string>();
                    if (entry.TryGetProperty("flags", out var flags))
                    {
                        foreach (var flag in flags.EnumerateObject())
                            tags.Add(flag.Name);
                    }
                    if (package.TryGetProperty("keywords", out var keywords))
                    {
                        foreach (var kw in keywords.EnumerateArray())
                        {
                            var kwStr = kw.GetString();
                            if (!string.IsNullOrEmpty(kwStr))
                                tags.Add(kwStr);
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = npmUrl,
                        Title = name,
                        Content = description,
                        Author = author,
                        PublishedDate = publishedDate,
                        Tags = tags,
                        Source = homepage,
                        Metadata = $"v{version}",
                        Engine = Name,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse package", Name);
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
