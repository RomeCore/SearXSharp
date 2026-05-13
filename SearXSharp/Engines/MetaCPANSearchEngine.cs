using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine for MetaCPAN (metacpan.org) - Perl package archive.
/// Uses the official MetaCPAN JSON API (Elasticsearch-based).
/// Based on SearXNG's metacpan.py.
/// </summary>
public class MetaCPANSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://fastapi.metacpan.org/v1/file/_search";
    private const int _resultsPerPage = 20;

    /// <inheritdoc />
    public override string Name => "metacpan";

    /// <inheritdoc />
    public override string DisplayName => "MetaCPAN";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Packages };

    /// <inheritdoc />
    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public MetaCPANSearchEngine() : base() { }
    public MetaCPANSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var queryData = new
            {
                query = new
                {
                    multi_match = new
                    {
                        type = "most_fields",
                        fields = new[] { "documentation", "documentation.*" },
                        analyzer = "camelcase",
                        query = query.Query,
                    }
                },
                filter = new
                {
                    @bool = new
                    {
                        must = new object[]
                        {
                            new { exists = new { field = "documentation" } },
                            new { term = new { status = "latest" } },
                            new { term = new { indexed = 1 } },
                            new { term = new { authorized = 1 } },
                        }
                    }
                },
                sort = new object[]
                {
                    new { _score = new { order = "desc" } },
                    new { date = new { order = "desc" } },
                },
                _source = new[] { "documentation", "abstract" },
                size = _resultsPerPage,
                from = (query.Page - 1) * _resultsPerPage,
            };

            var json = JsonSerializer.Serialize(queryData);
            using var request = CreateJsonPostRequest(_searchUrl, json);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var respJson = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(respJson);

            _logger.Debug("{Engine}: Parsed {Count} Perl module results", Name, results.Count);
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
            var hits = doc.RootElement.GetProperty("hits").GetProperty("hits");

            foreach (var hit in hits.EnumerateArray())
            {
                var source = hit.GetProperty("_source");
                var module = source.GetProperty("documentation").GetString() ?? "";
                var abstract = source.TryGetProperty("abstract", out var abs) ? abs.GetString() ?? "" : "";

                results.Add(new SearchResult
                {
                    Url = "https://metacpan.org/pod/" + module,
                    Title = module,
                    Content = abstract,
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
