using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Openverse (openverse.org).
/// Openverse (formerly Creative Commons Search) provides freely usable images.
/// Uses the official Openverse API (no key required).
/// Based on SearXNG's openverse.py.
/// </summary>
public class OpenverseSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://api.openverse.org/v1/images/";

    /// <inheritdoc />
    public override string Name => "openverse";

    /// <inheritdoc />
    public override string DisplayName => "Openverse";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

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

    public OpenverseSearchEngine() : base() { }
    public OpenverseSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _baseUrl + $"?page={query.Page}&page_size=20&format=json&q={Uri.EscapeDataString(query.Query)}";

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

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var url = item.TryGetProperty("foreign_landing_url", out var urlEl)
                        ? urlEl.GetString() ?? "" : "";
                    var title = item.TryGetProperty("title", out var titleEl)
                        ? titleEl.GetString() ?? "" : "";
                    var imgSrc = item.TryGetProperty("url", out var imgEl)
                        ? imgEl.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(imgSrc))
                        continue;

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        ImgSrc = imgSrc,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse image result", Name);
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
