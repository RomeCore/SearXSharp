using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for NuGet (.NET package registry).
/// Uses NuGet's official search API.
/// </summary>
public class NuGetSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://azuresearch-usnc.nuget.org/query";
    private const string _baseUrl = "https://www.nuget.org/packages";

    /// <inheritdoc />
    public override string Name => "nuget";

    /// <inheritdoc />
    public override string DisplayName => "NuGet";

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

    public NuGetSearchEngine() : base() { }
    public NuGetSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var skip = (query.Page - 1) * 20;
            var url = $"{_searchUrl}?q={Uri.EscapeDataString(query.Query)}&skip={skip}&take=20&prerelease=false";

            var request = CreateGetRequest(url);
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

            if (!root.TryGetProperty("data", out var data))
                return CreateResultList(results);

            foreach (var item in data.EnumerateArray())
            {
                try
                {
                    var id = item.GetProperty("id").GetString() ?? "";
                    var version = item.GetProperty("version").GetString() ?? "";
                    var url = $"{_baseUrl}/{id}";
                    var description = "";
                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        description = desc.GetString() ?? "";

                    var totalDownloads = 0L;
                    if (item.TryGetProperty("totalDownloads", out var downloads))
                        totalDownloads = downloads.GetInt64();

                    var tags = new List<string>();
                    if (item.TryGetProperty("tags", out var tagsEl))
                        tags = tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").ToList();

                    var authors = "";
                    if (item.TryGetProperty("authors", out var auth) && auth.ValueKind == JsonValueKind.String)
                        authors = auth.GetString() ?? "";

                    var iconUrl = "";
                    if (item.TryGetProperty("iconUrl", out var icon) && icon.ValueKind == JsonValueKind.String)
                        iconUrl = icon.GetString() ?? "";

                    var content = $"v{version} | Downloads: {totalDownloads:N0}";
                    if (!string.IsNullOrEmpty(authors)) content += $" | By: {authors}";
                    if (!string.IsNullOrEmpty(description)) content += $" | {description}";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = id,
                        Content = content,
                        Thumbnail = iconUrl,
                        Tags = tags,
                        Score = totalDownloads,
                        Author = authors,
                        Engine = Name,
                        Category = SearchCategory.Packages,
                        Metadata = $"v{version}",
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
