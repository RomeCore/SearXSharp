using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Hex.pm (hex.pm) - Elixir/Erlang package registry.
/// Based on SearXNG's hex.py.
/// </summary>
public class HexSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://hex.pm/api/packages";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "hex";

    /// <inheritdoc />
    public override string DisplayName => "Hex.pm";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Packages };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public HexSearchEngine() : base() { }
    public HexSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["page"] = query.Page.ToString(),
                ["per_page"] = _pageSize.ToString(),
                ["sort"] = "recent_downloads",
                ["search"] = query.Query,
            };

            var url = _searchUrl + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} Hex packages", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            // Hex API returns array directly
            if (root.ValueKind != JsonValueKind.Array) return results;

            foreach (var package in root.EnumerateArray())
            {
                var name = package.GetProperty("name").GetString() ?? "";
                var htmlUrl = package.GetProperty("html_url").GetString() ?? "";
                var meta = package.GetProperty("meta");
                var description = meta.TryGetProperty("description", out var desc)
                    ? desc.GetString() ?? "" : "";
                var version = meta.TryGetProperty("latest_version", out var ver)
                    ? ver.GetString() ?? "" : "";
                var docsUrl = package.TryGetProperty("docs_html_url", out var docs)
                    ? docs.GetString() ?? "" : null;

                var maintainers = meta.TryGetProperty("maintainers", out var maint)
                    ? string.Join(", ", maint.EnumerateArray().Select(m => m.GetString() ?? ""))
                    : "";

                var licenses = meta.TryGetProperty("licenses", out var lic)
                    ? string.Join(", ", lic.EnumerateArray().Select(l => l.GetString() ?? ""))
                    : "";

                DateTime? publishedDate = null;
                if (package.TryGetProperty("updated_at", out var updated))
                {
                    var dateStr = updated.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
                        publishedDate = dt;
                }

                var content = description;
                if (!string.IsNullOrEmpty(version))
                    content = $"v{version}" + (!string.IsNullOrEmpty(content) ? $" - {content}" : "");

                results.Add(new SearchResult
                {
                    Url = htmlUrl,
                    Title = name,
                    Content = content,
                    Author = maintainers,
                    PublishedDate = publishedDate,
                    Metadata = licenses,
                    Source = docsUrl,
                    Engine = Name,
                    Category = SearchCategory.Packages,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return results;
    }
}
