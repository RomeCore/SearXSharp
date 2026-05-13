using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Apple App Store.
/// Uses the iTunes Search API (no API key required).
/// Based on SearXNG's apple_app_store.py.
/// </summary>
public class AppleAppStoreSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://itunes.apple.com/search";

    /// <inheritdoc />
    public override string Name => "apple_app_store";

    /// <inheritdoc />
    public override string DisplayName => "Apple App Store";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.Packages };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public AppleAppStoreSearchEngine() : base() { }
    public AppleAppStoreSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var explicitParam = query.SafeSearch > SafeSearchLevel.None ? "No" : "Yes";
            var url = _searchUrl + "?" + string.Join("&", new[]
            {
                $"term={Uri.EscapeDataString(query.Query)}",
                "media=software",
                $"explicit={explicitParam}",
            });

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
                    var url = item.GetProperty("trackViewUrl").GetString() ?? "";
                    var title = item.GetProperty("trackName").GetString() ?? "";
                    var description = item.GetProperty("description").GetString() ?? "";
                    var thumbnail = item.GetProperty("artworkUrl100").GetString() ?? "";
                    var seller = item.TryGetProperty("sellerName", out var sn)
                        ? sn.GetString() ?? "" : "";

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("currentVersionReleaseDate", out var dateEl))
                    {
                        if (DateTime.TryParse(dateEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        Author = seller,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result item", Name);
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
