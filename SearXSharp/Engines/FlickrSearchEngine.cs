using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Flickr (images).
/// Uses HTML scraping with modelExport JSON extraction.
/// Based on SearXNG's flickr_noapi.py.
/// </summary>
public class FlickrSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://www.flickr.com/search";
    private const string _photoUrl = "https://www.flickr.com/photos/{0}/{1}";

    /// <inheritdoc />
    public override string Name => "flickr";

    /// <inheritdoc />
    public override string DisplayName => "Flickr";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    // Image sizes from largest to smallest
    private static readonly string[] _imageSizes = { "o", "k", "h", "b", "c", "z", "m", "n", "t", "q", "s" };

    private static readonly Dictionary<TimeRange, int> _timeRangeDict = new()
    {
        [TimeRange.Day] = 60 * 60 * 24,
        [TimeRange.Week] = 60 * 60 * 24 * 7,
        [TimeRange.Month] = 60 * 60 * 24 * 7 * 4,
        [TimeRange.Year] = 60 * 60 * 24 * 7 * 52,
    };

    private static readonly Regex _modelExportRegex = new(
        @"^\s*modelExport:\s*({.*}),$", RegexOptions.Multiline | RegexOptions.Compiled);

    public FlickrSearchEngine() : base() { }
    public FlickrSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _searchUrl + "?text=" + Uri.EscapeDataString(query.Query)
                + "&page=" + query.Page;

            if (query.TimeRange.HasValue && _timeRangeDict.TryGetValue(query.TimeRange.Value, out var seconds))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                url += "&min_upload_date=" + now
                    + "&max_upload_date=" + (now - seconds);
            }

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

        var match = _modelExportRegex.Match(html);
        if (!match.Success)
            return CreateResultList(results);

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;

            if (!root.TryGetProperty("legend", out var legend) || !legend.EnumerateArray().Any())
                return CreateResultList(results);

            if (!root.TryGetProperty("main", out var main))
                return CreateResultList(results);

            var mainArr = main.EnumerateArray().ToList();
            var legendArr = legend.EnumerateArray().ToList();

            foreach (var index in legendArr)
            {
                try
                {
                    if (index.GetArrayLength() != 8)
                        continue;

                    var indices = index.EnumerateArray().Select(e => e.GetInt32()).ToArray();

                    // Navigate the nested structure: main[0][idx1][idx2][idx3][idx4][idx5][idx6][idx7]
                    var photo = mainArr[indices[0]]
                        .EnumerateArray().ElementAt(indices[1])
                        .EnumerateArray().ElementAt(indices[2])
                        .EnumerateArray().ElementAt(indices[3])
                        .EnumerateArray().ElementAt(indices[4])
                        .EnumerateArray().ElementAt(indices[5])
                        .EnumerateArray().ElementAt(indices[6])
                        .EnumerateArray().ElementAt(indices[7]);

                    var author = GetStringProperty(photo, "realname");
                    var username = GetStringProperty(photo, "username");
                    var source = string.IsNullOrEmpty(username) ? "Flickr" : username + " @ Flickr";
                    var title = GetStringProperty(photo, "title");
                    var content = HtmlToText(GetStringProperty(photo, "description"));

                    string? imgSrc = null;
                    string? thumbnailSrc = null;
                    string? resolution = null;

                    if (photo.TryGetProperty("sizes", out var sizes)
                        && sizes.TryGetProperty("data", out var sizesData))
                    {
                        foreach (var size in _imageSizes)
                        {
                            if (sizesData.TryGetProperty(size, out var sizeEntry)
                                && sizeEntry.TryGetProperty("data", out var sizeData))
                            {
                                imgSrc = GetStringProperty(sizeData, "url");

                                var w = 0;
                                var h = 0;
                                if (sizeData.TryGetProperty("width", out var wEl)) w = wEl.GetInt32();
                                if (sizeData.TryGetProperty("height", out var hEl)) h = hEl.GetInt32();
                                if (w > 0 && h > 0)
                                    resolution = $"{w} x {h}";

                                break; // Take the largest available
                            }
                        }

                        // Thumbnail: prefer 'n' size, fallback 'z'
                        if (sizesData.TryGetProperty("n", out var thumbN)
                            && thumbN.TryGetProperty("data", out var thumbNData))
                            thumbnailSrc = GetStringProperty(thumbNData, "url");
                        else if (sizesData.TryGetProperty("z", out var thumbZ)
                            && thumbZ.TryGetProperty("data", out var thumbZData))
                            thumbnailSrc = GetStringProperty(thumbZData, "url");
                        else
                            thumbnailSrc = imgSrc;
                    }

                    string? ownerNsid = null;
                    if (photo.TryGetProperty("ownerNsid", out var owner))
                        ownerNsid = owner.GetString();

                    var photoUrl = ownerNsid != null
                        ? string.Format(_photoUrl, ownerNsid, GetStringProperty(photo, "id"))
                        : imgSrc ?? "";

                    results.Add(new SearchResult
                    {
                        Url = photoUrl,
                        Title = SanitizeText(title),
                        Content = SanitizeText(content),
                        Author = SanitizeText(author),
                        Source = SanitizeText(source),
                        ImgSrc = imgSrc,
                        Thumbnail = thumbnailSrc,
                        Resolution = resolution,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse legend entry", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse modelExport JSON", Name);
        }

        return CreateResultList(results);
    }

    private static string GetStringProperty(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }

    private static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Remove invalid XML characters and normalize
        return System.Text.Encoding.UTF8.GetString(
            System.Text.Encoding.UTF8.GetBytes(text));
    }

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // Simple HTML tag removal
        return Regex.Replace(html, "<[^>]*>", " ").Trim();
    }
}
