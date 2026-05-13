using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Steam store.
/// Uses Steam's internal store search API (no key required).
/// Based on SearXNG's steam.py.
/// </summary>
public class SteamSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://store.steampowered.com";
    private const string _searchApi = "https://store.steampowered.com/api/storesearch";

    /// <inheritdoc />
    public override string Name => "steam";

    /// <inheritdoc />
    public override string DisplayName => "Steam";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Files };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 0;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public SteamSearchEngine() : base() { }
    public SteamSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = $"{_searchApi}?term={Uri.EscapeDataString(query.Query)}&cc=us&l=en";
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

            if (!root.TryGetProperty("items", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var appId = item.GetProperty("id").GetInt32();
                    var name = item.GetProperty("name").GetString() ?? "";
                    var url = $"{_baseUrl}/app/{appId}";
                    var thumbnail = item.GetProperty("tiny_image").GetString() ?? "";

                    var price = 0m;
                    var currency = "USD";
                    if (item.TryGetProperty("price", out var priceEl))
                    {
                        currency = priceEl.GetProperty("currency").GetString() ?? "USD";
                        price = priceEl.GetProperty("final").GetDecimal() / 100;
                    }

                    var platforms = new List<string>();
                    if (item.TryGetProperty("platforms", out var platformsEl))
                    {
                        foreach (var plat in platformsEl.EnumerateObject())
                        {
                            if (plat.Value.GetBoolean())
                                platforms.Add(plat.Name);
                        }
                    }

                    var content = $"Price: {price:F2} {currency} | Platforms: {string.Join(", ", platforms)}";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = name,
                        Content = content,
                        Thumbnail = thumbnail,
                        Score = (double)price,
                        Engine = Name,
                        Category = SearchCategory.IT,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse item", Name);
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
