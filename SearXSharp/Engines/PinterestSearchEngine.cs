using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Pinterest (image discovery).
/// Uses Pinterest's internal resource API.
/// Based on SearXNG's pinterest.py.
/// </summary>
public class PinterestSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.pinterest.com";

    /// <inheritdoc />
    public override string Name => "pinterest";

    /// <inheritdoc />
    public override string DisplayName => "Pinterest";

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

    private string? _bookmark;

    public PinterestSearchEngine() : base() { }
    public PinterestSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var options = new
            {
                query = query.Query,
                bookmarks = new[] { _bookmark ?? "" },
            };

            var payload = JsonSerializer.Serialize(new
            {
                options,
                context = new { },
            });

            var url = $"{_baseUrl}/resource/BaseSearchResource/get/?data={Uri.EscapeDataString(payload)}";
            using var request = CreateGetRequest(url);

            request.Headers.TryAddWithoutValidation("X-Pinterest-AppState", "active");
            request.Headers.TryAddWithoutValidation("X-Pinterest-Source-Url", "/ideas/");
            request.Headers.TryAddWithoutValidation("X-Pinterest-PWS-Handler", "www/ideas.js");

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

            var resourceResponse = root.GetProperty("resource_response");

            // Extract bookmark for pagination
            if (resourceResponse.TryGetProperty("bookmark", out var bookmarkEl)
                && bookmarkEl.ValueKind == JsonValueKind.String)
            {
                _bookmark = bookmarkEl.GetString();
            }

            if (!resourceResponse.TryGetProperty("data", out var data)
                || !data.TryGetProperty("results", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    if (item.GetProperty("type").GetString() == "story")
                        continue;

                    var images = item.GetProperty("images");
                    var mainImage = images.GetProperty("orig");

                    var url = "";
                    if (item.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.String)
                        url = link.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(url))
                        url = $"{_baseUrl}/pin/{item.GetProperty("id").GetString()}/";

                    var title = "";
                    if (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                        title = t.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(title) && item.TryGetProperty("grid_title", out var gt))
                        title = gt.GetString() ?? "";

                    var content = "";
                    if (item.TryGetProperty("rich_summary", out var summary)
                        && summary.TryGetProperty("display_description", out var desc))
                        content = desc.GetString() ?? "";

                    var source = "";
                    if (item.TryGetProperty("rich_summary", out var rs)
                        && rs.TryGetProperty("site_name", out var sn))
                        source = sn.GetString() ?? "";

                    var author = "";
                    if (item.TryGetProperty("pinner", out var pinner))
                    {
                        var fullName = pinner.GetProperty("full_name").GetString() ?? "";
                        var username = pinner.GetProperty("username").GetString() ?? "";
                        author = $"{fullName} ({username})";
                    }

                    var thumbnail = images.GetProperty("236x").GetProperty("url").GetString() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Thumbnail = thumbnail,
                        ImgSrc = mainImage.GetProperty("url").GetString(),
                        Resolution = $"{mainImage.GetProperty("width").GetInt32()}x{mainImage.GetProperty("height").GetInt32()}",
                        Author = author,
                        Source = source,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
