using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Mwmbl (mwmbl.org).
/// Mwmbl is a non-profit, ad-free, free-libre search engine.
/// Uses the official API (no key required).
/// Based on SearXNG's mwmbl.py.
/// </summary>
public class MwmblSearchEngine : SearchEngineBase
{
    private const string _apiUrl = "https://api.mwmbl.org/api/v1";

    /// <inheritdoc />
    public override string Name => "mwmbl";

    /// <inheritdoc />
    public override string DisplayName => "Mwmbl";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public MwmblSearchEngine() : base() { }
    public MwmblSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = $"{_apiUrl}/search/?s={Uri.EscapeDataString(query.Query)}";

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

            foreach (var result in root.EnumerateArray())
            {
                try
                {
                    var url = result.GetProperty("url").GetString() ?? "";

                    var title = "";
                    if (result.TryGetProperty("title", out var titleEl))
                    {
                        title = string.Join("", titleEl.EnumerateArray().Select(t => t.GetProperty("value").GetString() ?? ""));
                    }

                    var content = "";
                    if (result.TryGetProperty("extract", out var extractEl))
                    {
                        content = string.Join("", extractEl.EnumerateArray().Select(e => e.GetProperty("value").GetString() ?? ""));
                    }

                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(title))
                    {
                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = content,
                            Engine = Name,
                            Category = SearchCategory.Web,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result", Name);
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
