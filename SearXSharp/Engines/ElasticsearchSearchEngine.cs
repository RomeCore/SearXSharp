using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Elasticsearch.
/// Queries an Elasticsearch instance via its REST API.
/// Based on SearXNG's elasticsearch.py.
/// </summary>
public class ElasticsearchSearchEngine : SearchEngineBase
{
    /// <summary>
    /// Base URL of the Elasticsearch instance (e.g., "http://localhost:9200").
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:9200";

    /// <summary>
    /// Elasticsearch index to search.
    /// </summary>
    public string Index { get; set; } = "";

    /// <summary>
    /// Query type: "match", "simple_query_string", "term", "terms".
    /// </summary>
    public string QueryType { get; set; } = "simple_query_string";

    /// <summary>
    /// Whether to show metadata (index, id, score) in results.
    /// </summary>
    public bool ShowMetadata { get; set; } = false;

    /// <inheritdoc />
    public override string Name => "elasticsearch";

    /// <inheritdoc />
    public override string DisplayName => "Elasticsearch";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.IT };

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

    public ElasticsearchSearchEngine() : base() { }
    public ElasticsearchSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var pageSize = 10;
            var from = (query.Page - 1) * pageSize;

            object esQuery;
            switch (QueryType.ToLowerInvariant())
            {
                case "simple_query_string":
                    esQuery = new
                    {
                        query = new { simple_query_string = new { query = query.Query } },
                        from,
                        size = pageSize,
                    };
                    break;
                case "match":
                    var parts = query.Query.Split(':', 2);
                    if (parts.Length != 2)
                        return CreateErrorResult("format must be 'key:value'");
                    esQuery = new
                    {
                        query = new { match = new Dictionary<string, object> { { parts[0], new { query = parts[1] } } } },
                        from,
                        size = pageSize,
                    };
                    break;
                case "term":
                    var termParts = query.Query.Split(':', 2);
                    if (termParts.Length != 2)
                        return CreateErrorResult("format must be 'key:value'");
                    esQuery = new
                    {
                        query = new { term = new Dictionary<string, string> { { termParts[0], termParts[1] } } },
                        from,
                        size = pageSize,
                    };
                    break;
                default:
                    return CreateErrorResult($"unsupported query type: {QueryType}");
            }

            var jsonContent = JsonSerializer.Serialize(esQuery);
            var url = $"{BaseUrl.TrimEnd('/')}/{Index}/_search";

            using var request = CreateJsonPostRequest(url, jsonContent);
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
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            if (root.TryGetProperty("error", out var errorEl))
            {
                _logger.Error("{Engine}: ES error: {Error}", Name, errorEl.GetString());
                return CreateResultList(results);
            }

            if (!root.TryGetProperty("hits", out var hits)
                || !hits.TryGetProperty("hits", out var hitList))
                return CreateResultList(results);

            foreach (var hit in hitList.EnumerateArray())
            {
                try
                {
                    if (!hit.TryGetProperty("_source", out var source))
                        continue;

                    var contentParts = new List<string>();
                    foreach (var prop in source.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            contentParts.Add($"{prop.Name}: {prop.Value.GetString()}");
                        else
                            contentParts.Add($"{prop.Name}: {prop.Value.GetRawText()}");
                    }

                    var score = hit.TryGetProperty("_score", out var scoreEl)
                        ? scoreEl.GetDouble() : 0;

                    var metadata = ShowMetadata
                        ? $"index: {hit.GetProperty("_index").GetString()}, id: {hit.GetProperty("_id").GetString()}, score: {score:F2}"
                        : "";

                    var title = contentParts.Count > 0 ? contentParts[0] : "ES Result";
                    if (contentParts.Count > 1)
                        contentParts.RemoveAt(0);

                    results.Add(new SearchResult
                    {
                        Url = $"{BaseUrl.TrimEnd('/')}/{Index}/_doc/{hit.GetProperty("_id").GetString()}",
                        Title = title.Length > 200 ? title[..200] : title,
                        Content = string.Join(" | ", contentParts),
                        Score = score,
                        Metadata = metadata,
                        Engine = Name,
                        Category = SearchCategory.IT,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse hit", Name);
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
