using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Lucide (lucide.dev) - open source icon library.
/// Searches through the Lucide icon collection. All icons are copyleft and free to use.
/// Based on SearXNG's lucide.py.
/// </summary>
public class LucideSearchEngine : SearchEngineBase
{
    private const string _cdnBaseUrl = "https://cdn.jsdelivr.net/npm/lucide-static";
    private const string _tagsUrl = _cdnBaseUrl + "/tags.json";

    /// <inheritdoc />
    public override string Name => "lucide";

    /// <inheritdoc />
    public override string DisplayName => "Lucide";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    public override bool SupportsPaging => false;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 1;
    public override double Timeout => 10.0;

    public LucideSearchEngine() : base() { }
    public LucideSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateGetRequest(_tagsUrl);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json, query.Query);

            _logger.Debug("{Engine}: Found {Count} matching icons for '{Query}'", Name, results.Count, query.Query);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseJson(string json, string query)
    {
        var results = new List<SearchResult>();
        var queryParts = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var icon in root.EnumerateObject())
            {
                var iconName = icon.Name;
                var tags = icon.Value.EnumerateArray()
                    .Select(t => t.GetString() ?? "")
                    .ToList();

                // Check if any query part matches name or tags
                var match = false;
                foreach (var part in queryParts)
                {
                    if (iconName.Contains(part))
                    {
                        match = true;
                        break;
                    }
                    foreach (var tag in tags)
                    {
                        if (tag.Contains(part))
                        {
                            match = true;
                            break;
                        }
                    }
                    if (match) break;
                }
                if (!match && queryParts.Length > 0) continue;

                var imgSrc = $"{_cdnBaseUrl}/icons/{iconName}.svg";

                results.Add(new SearchResult
                {
                    Url = imgSrc,
                    Title = iconName,
                    Content = string.Join(", ", tags),
                    ImgSrc = imgSrc,
                    Thumbnail = imgSrc,
                    Engine = Name,
                    Type = SearchResultType.Image,
                    Category = SearchCategory.Images,
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
