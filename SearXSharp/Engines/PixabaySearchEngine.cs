using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Pixabay (royalty-free images and videos).
/// Uses Pixabay's internal JSON API (not the official API key).
/// Based on SearXNG's pixabay.py.
/// </summary>
public class PixabaySearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://pixabay.com";

    /// <inheritdoc />
    public override string Name => "pixabay";

    /// <inheritdoc />
    public override string DisplayName => "Pixabay";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 20;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    private static readonly Dictionary<SafeSearchLevel, string> _safeSearchMap = new()
    {
        [SafeSearchLevel.None] = "off",
        [SafeSearchLevel.Moderate] = "1",
        [SafeSearchLevel.Strict] = "1",
    };

    private static readonly Dictionary<TimeRange, string> _timeRangeMap = new()
    {
        [TimeRange.Day] = "1d",
        [TimeRange.Week] = "1w",
        [TimeRange.Month] = "1m",
        [TimeRange.Year] = "1y",
    };

    public PixabaySearchEngine() : base() { }
    public PixabaySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, string>
            {
                ["pagi"] = query.Page.ToString(),
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var date))
                args["date"] = date;

            var searchUrl = $"{_baseUrl}/images/search/{Uri.EscapeDataString(query.Query)}/?"
                + string.Join("&", args.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(searchUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("x-bootstrap-cache-miss", "1");
            request.Headers.TryAddWithoutValidation("x-fetch-bootstrap", "1");
            request.Headers.UserAgent.TryParseAdd(GetRandomUserAgent() + " Pixabay");

            // Use separate HttpClient configuration to prevent redirects
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate
                    | System.Net.DecompressionMethods.Brotli,
            };
            using var redirectClient = new HttpClient(handler);
            redirectClient.Timeout = TimeSpan.FromSeconds(Timeout + 5);

            var response = await redirectClient.SendAsync(request, ct);

            // If redirected to first page (no results), return empty
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                return CreateResultList(Array.Empty<SearchResult>());

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

            if (!root.TryGetProperty("page", out var page)
                || !page.TryGetProperty("results", out var pageResults))
                return CreateResultList(results);

            foreach (var item in pageResults.EnumerateArray())
            {
                try
                {
                    var mediaType = item.GetProperty("mediaType").GetString();
                    var href = item.GetProperty("href").GetString() ?? "";
                    var name = item.GetProperty("name").GetString() ?? "";

                    var description = "";
                    if (item.TryGetProperty("description", out var desc))
                        description = desc.GetString() ?? "";

                    var sources = item.GetProperty("sources");

                    switch (mediaType)
                    {
                        case "photo":
                        case "illustration":
                        case "vector":
                        {
                            var thumbnails = sources.EnumerateObject()
                                .Select(s => s.Value.GetString())
                                .Where(s => s != null)
                                .ToList();

                            results.Add(new SearchResult
                            {
                                Url = _baseUrl + href,
                                Title = name,
                                Content = description,
                                Thumbnail = thumbnails.FirstOrDefault(),
                                ImgSrc = thumbnails.LastOrDefault(),
                                Engine = Name,
                                Category = SearchCategory.Images,
                                Type = SearchResultType.Image,
                            });
                            break;
                        }
                        case "video":
                        {
                            var thumbnail = "";
                            string? iframeSrc = null;
                            TimeSpan? duration = null;

                            foreach (var src in sources.EnumerateObject())
                            {
                                var val = src.Value.GetString();
                                if (src.Name == "thumbnail") thumbnail = val ?? "";
                                else if (src.Name == "embed") iframeSrc = val;
                            }

                            if (item.TryGetProperty("duration", out var durEl))
                                duration = TimeSpan.FromSeconds(durEl.GetDouble());
                            if (item.TryGetProperty("uploadDate", out var uploadDate))
                            {
                                if (DateTime.TryParse(uploadDate.GetString(), out var parsedDate))
                                {
                                    // We don't have PublishedDate in a great shape yet,
                                    // but we'll try
                                }
                            }

                            results.Add(new SearchResult
                            {
                                Url = _baseUrl + href,
                                Title = name,
                                Content = description,
                                Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                                IframeSrc = iframeSrc,
                                Duration = duration,
                                Engine = Name,
                                Category = SearchCategory.Images,
                                Type = SearchResultType.Video,
                            });
                            break;
                        }
                    }
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
