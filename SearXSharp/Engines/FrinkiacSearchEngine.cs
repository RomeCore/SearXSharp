using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Frinkiac (frinkiac.com).
/// Frinkiac is a Simpsons screenshot search engine with memes.
/// Uses the unofficial Frinkiac API.
/// Based on SearXNG's frinkiac.py.
/// </summary>
public class FrinkiacSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://frinkiac.com/";

    /// <inheritdoc />
    public override string Name => "frinkiac";

    /// <inheritdoc />
    public override string DisplayName => "Frinkiac";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

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

    public FrinkiacSearchEngine() : base() { }
    public FrinkiacSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = $"{_baseUrl}api/search?q={Uri.EscapeDataString(query.Query)}";

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

            foreach (var item in root.EnumerateArray())
            {
                try
                {
                    var episode = item.GetProperty("Episode").GetString() ?? "";
                    var timestamp = item.GetProperty("Timestamp").GetString() ?? "";

                    var url = $"{_baseUrl}?p=caption&e={Uri.EscapeDataString(episode)}&t={Uri.EscapeDataString(timestamp)}";
                    var thumbnail = $"{_baseUrl}img/{episode}/{timestamp}/medium.jpg";
                    var imgSrc = $"{_baseUrl}img/{episode}/{timestamp}.jpg";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = episode,
                        Content = $"Frinkiac screenshot - {episode}",
                        Thumbnail = thumbnail,
                        ImgSrc = imgSrc,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
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
