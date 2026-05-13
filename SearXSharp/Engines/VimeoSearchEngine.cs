using SearXSharp.Models;
using System.Text.Json;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Vimeo.
/// Uses HTML scraping of Vimeo's search page, extracting embedded JSON data.
/// Based on SearXNG's vimeo.py.
/// </summary>
public class VimeoSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://vimeo.com";
    private const string _searchUrl = "https://vimeo.com/search/page:{0}?q={1}";

    /// <inheritdoc />
    public override string Name => "vimeo";

    /// <inheritdoc />
    public override string DisplayName => "Vimeo";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos };

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

    public VimeoSearchEngine() : base() { }
    public VimeoSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = string.Format(_searchUrl, query.Page, Uri.EscapeDataString(query.Query));
            var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseHtml(html);
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

    private SearchResultList ParseHtml(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            // Extract the JSON data blob from the page
            var dataStart = html.IndexOf("var data = ", StringComparison.Ordinal);
            if (dataStart < 0)
                return CreateResultList(results);

            dataStart += "var data = ".Length;
            var dataEnd = html.IndexOf(";\n", dataStart, StringComparison.Ordinal);
            if (dataEnd < 0)
                return CreateResultList(results);

            var json = html[dataStart..dataEnd];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("filtered", out var filtered)
                || !filtered.TryGetProperty("data", out var data))
                return CreateResultList(results);

            var position = 1;
            foreach (var item in data.EnumerateArray())
            {
                try
                {
                    // Each item has a type key (e.g., "clip", "video", etc.)
                    string? typeKey = null;
                    foreach (var prop in item.EnumerateObject())
                    {
                        typeKey = prop.Name;
                        break;
                    }

                    if (typeKey == null) continue;

                    var resultData = item.GetProperty(typeKey);

                    var uri = resultData.GetProperty("uri").GetString() ?? "";
                    var videoid = uri.Split('/')[^1];
                    var url = $"{_baseUrl}/{videoid}";
                    var title = resultData.GetProperty("name").GetString() ?? "";

                    var thumbnail = "";
                    if (resultData.TryGetProperty("pictures", out var pictures)
                        && pictures.TryGetProperty("sizes", out var sizes))
                    {
                        var sizeArray = sizes.EnumerateArray().ToList();
                        if (sizeArray.Count > 0)
                            thumbnail = sizeArray[^1].GetProperty("link").GetString() ?? "";
                    }

                    var iframeSrc = $"https://player.vimeo.com/video/{videoid}";

                    DateTime? publishedDate = null;
                    if (resultData.TryGetProperty("created_time", out var created))
                    {
                        if (DateTime.TryParse(created.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Thumbnail = thumbnail,
                        IframeSrc = iframeSrc,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse video item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON data", Name);
        }

        return CreateResultList(results);
    }
}
