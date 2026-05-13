using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Fyyd (fyyd.de) - podcast search.
/// Fyyd is a podcast directory and search engine.
/// Based on SearXNG's fyyd.py.
/// </summary>
public class FyydSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://api.fyyd.de";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "fyyd";

    /// <inheritdoc />
    public override string DisplayName => "Fyyd";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Music, SearchCategory.General };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public FyydSearchEngine() : base() { }
    public FyydSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["term"] = query.Query,
                ["count"] = _pageSize.ToString(),
                ["page"] = (query.Page - 1).ToString(),
            };

            var url = _baseUrl + "/0.2/search/podcast?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} podcast results", Name, results.Count);
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
            var data = doc.RootElement.GetProperty("data");

            foreach (var item in data.EnumerateArray())
            {
                var title = item.GetProperty("title").GetString() ?? "";
                var description = item.GetProperty("description").GetString() ?? "";
                var htmlUrl = item.GetProperty("htmlURL").GetString() ?? "";
                var imageUrl = item.GetProperty("smallImageURL").GetString() ?? "";
                var rank = item.TryGetProperty("rank", out var r) ? r.GetInt32() : 0;
                var episodeCount = item.TryGetProperty("episode_count", out var ec) ? ec.GetInt32() : 0;

                DateTime? publishedDate = null;
                if (item.TryGetProperty("status_since", out var status))
                {
                    var dateStr = status.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
                        publishedDate = dt;
                }

                if (string.IsNullOrEmpty(title)) continue;

                results.Add(new SearchResult
                {
                    Url = htmlUrl,
                    Title = title,
                    Content = description,
                    Thumbnail = imageUrl,
                    PublishedDate = publishedDate,
                    Metadata = $"Rank: {rank} | {episodeCount} episodes",
                    Engine = Name,
                    Category = SearchCategory.Music,
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
