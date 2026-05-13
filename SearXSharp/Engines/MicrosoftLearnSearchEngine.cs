using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Microsoft Learn (learn.microsoft.com).
/// Microsoft Learn is Microsoft's technical knowledge base with documentation, tutorials and training.
/// Based on SearXNG's microsoft_learn.py.
/// </summary>
public class MicrosoftLearnSearchEngine : SearchEngineBase
{
    private const string _searchApi = "https://learn.microsoft.com/api/search";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "microsoft_learn";

    /// <inheritdoc />
    public override string DisplayName => "Microsoft Learn";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT };

    /// <inheritdoc />
    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public MicrosoftLearnSearchEngine() : base() { }
    public MicrosoftLearnSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var locale = string.IsNullOrEmpty(query.Language) || query.Language == "all"
                ? "en-us"
                : query.Language;

            var queryParams = new Dictionary<string, string>
            {
                ["search"] = query.Query,
                ["locale"] = locale,
                ["scoringprofile"] = "semantic-answers",
                ["facet"] = "category",
                ["$top"] = "10",
                ["$skip"] = ((query.Page - 1) * _pageSize).ToString(),
                ["expandScope"] = "true",
                ["includeQuestion"] = "false",
                ["applyOperator"] = "false",
                ["partnerId"] = "LearnSite",
            };

            var url = _searchApi + "?" + string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} MS Learn results", Name, results.Count);
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
            var resultsArray = doc.RootElement.GetProperty("results");

            foreach (var item in resultsArray.EnumerateArray())
            {
                var url = item.GetProperty("url").GetString() ?? "";
                var title = item.GetProperty("title").GetString() ?? "";
                var description = item.TryGetProperty("description", out var desc)
                    ? desc.GetString() ?? ""
                    : "";

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(title)) continue;

                results.Add(new SearchResult
                {
                    Url = url,
                    Title = title,
                    Content = description,
                    Engine = Name,
                    Category = SearchCategory.IT,
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
