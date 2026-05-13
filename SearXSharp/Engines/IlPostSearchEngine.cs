using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Il Post (ilpost.it) - Italian online newspaper.
/// Based on SearXNG's il_post.py.
/// </summary>
public class IlPostSearchEngine : SearchEngineBase
{
    private const string _searchApi = "https://api.ilpost.org/search/api/site_search/";

    /// <inheritdoc />
    public override string Name => "il_post";

    /// <inheritdoc />
    public override string DisplayName => "Il Post";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.News };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => true;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Month] = "pub_date:ultimi_30_giorni",
        [TimeRange.Year] = "pub_date:ultimo_anno",
    };

    public IlPostSearchEngine() : base() { }
    public IlPostSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var filters = "ctype:articoli";
            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var tr))
                filters += ";" + tr;

            var queryParams = new Dictionary<string, string>
            {
                ["qs"] = query.Query,
                ["pg"] = query.Page.ToString(),
                ["sort"] = "date_d",
                ["filters"] = filters,
            };

            var url = _searchApi + string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} news results", Name, results.Count);
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
            var docs = doc.RootElement.GetProperty("docs");

            foreach (var item in docs.EnumerateArray())
            {
                var link = item.GetProperty("link").GetString() ?? "";
                var title = item.GetProperty("title").GetString() ?? "";
                var summary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                var image = item.TryGetProperty("image", out var img) ? img.GetString() : null;

                if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(title)) continue;

                results.Add(new SearchResult
                {
                    Url = link,
                    Title = title,
                    Content = summary,
                    Thumbnail = image,
                    Engine = Name,
                    Type = SearchResultType.News,
                    Category = SearchCategory.News,
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
