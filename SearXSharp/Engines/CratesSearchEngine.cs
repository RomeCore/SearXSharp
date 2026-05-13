using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for crates.io (Rust package registry).
/// Uses official crates.io API.
/// Based on SearXNG's crates.py.
/// </summary>
public class CratesSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://crates.io/api/v1/crates";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "crates";

    /// <inheritdoc />
    public override string DisplayName => "Crates.io";

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

    public CratesSearchEngine() : base() { }
    public CratesSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["page"] = query.Page.ToString(),
                ["q"] = query.Query,
                ["per_page"] = _pageSize.ToString(),
            };

            var url = _searchUrl + "?" + string.Join("&", args.Select(kv =>
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

            if (!root.TryGetProperty("crates", out var crates))
                return CreateResultList(results);

            foreach (var package in crates.EnumerateArray())
            {
                try
                {
                    var name = package.GetProperty("name").GetString() ?? "";
                    var description = "";
                    if (package.TryGetProperty("description", out var desc))
                        description = desc.GetString() ?? "";

                    var newestVersion = "";
                    if (package.TryGetProperty("newest_version", out var nv) && nv.ValueKind == JsonValueKind.String)
                        newestVersion = nv.GetString() ?? "";
                    if (string.IsNullOrEmpty(newestVersion) && package.TryGetProperty("max_version", out var mv))
                        newestVersion = mv.GetString() ?? "";
                    if (string.IsNullOrEmpty(newestVersion) && package.TryGetProperty("max_stable_version", out var msv))
                        newestVersion = msv.GetString() ?? "";

                    DateTime? publishedDate = null;
                    if (package.TryGetProperty("updated_at", out var updated))
                    {
                        if (DateTime.TryParse(updated.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    var keywords = new List<string>();
                    if (package.TryGetProperty("keywords", out var kw))
                    {
                        foreach (var k in kw.EnumerateArray())
                        {
                            var kStr = k.GetString();
                            if (!string.IsNullOrEmpty(kStr))
                                keywords.Add(kStr);
                        }
                    }

                    // Links
                    var links = new List<string>();
                    if (package.TryGetProperty("homepage", out var home) && home.ValueKind == JsonValueKind.String)
                        links.Add($"Homepage: {home.GetString()}");
                    if (package.TryGetProperty("documentation", out var docs) && docs.ValueKind == JsonValueKind.String)
                        links.Add($"Documentation: {docs.GetString()}");
                    if (package.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.String)
                        links.Add($"Source: {repo.GetString()}");

                    results.Add(new SearchResult
                    {
                        Url = $"https://crates.io/crates/{name}",
                        Title = name,
                        Content = description,
                        PublishedDate = publishedDate,
                        Tags = keywords,
                        Metadata = $"v{newestVersion}",
                        Source = links.Count > 0 ? string.Join(" | ", links) : null,
                        Engine = Name,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse crate", Name);
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
