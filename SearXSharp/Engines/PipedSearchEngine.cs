using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Piped (piped.video).
/// Piped is an alternative privacy-friendly YouTube frontend.
/// Uses the Piped backend API.
/// Based on SearXNG's piped.py.
/// </summary>
public class PipedSearchEngine : SearchEngineBase
{
    private const string _defaultBackend = "https://pipedapi.kavin.rocks";
    private const string _defaultFrontend = "https://piped.video";

    /// <summary>
    /// Backend API URL for Piped.
    /// </summary>
    public string BackendUrl { get; set; } = _defaultBackend;

    /// <summary>
    /// Frontend URL for Piped (used for links and embeds).
    /// </summary>
    public string FrontendUrl { get; set; } = _defaultFrontend;

    /// <inheritdoc />
    public override string Name => "piped";

    /// <inheritdoc />
    public override string DisplayName => "Piped";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Videos, SearchCategory.Music };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public PipedSearchEngine() : base() { }
    public PipedSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var backend = BackendUrl.TrimEnd('/');
            var args = $"?q={Uri.EscapeDataString(query.Query)}&filter=videos";
            var url = $"{backend}/search{args}";

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

            if (!root.TryGetProperty("items", out var items))
                return CreateResultList(results);

            var frontend = FrontendUrl.TrimEnd('/');

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var itemUrl = item.TryGetProperty("url", out var urlEl)
                        ? urlEl.GetString() ?? "" : "";
                    var title = item.TryGetProperty("title", out var titleEl)
                        ? titleEl.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(itemUrl)) continue;

                    var url = frontend + itemUrl;
                    var iframeSrc = $"{frontend}/embed{itemUrl}";

                    var description = item.TryGetProperty("shortDescription", out var descEl)
                        ? descEl.GetString() ?? "" : "";
                    var thumbnail = item.TryGetProperty("thumbnail", out var thumbEl)
                        ? thumbEl.GetString() ?? "" : "";
                    var uploader = item.TryGetProperty("uploaderName", out var uploaderEl)
                        ? uploaderEl.GetString() ?? "" : "";

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("uploaded", out var uploaded))
                    {
                        var uploadedMs = uploaded.GetInt64();
                        if (uploadedMs > 0)
                            publishedDate = DateTimeOffset.FromUnixTimeMilliseconds(uploadedMs).DateTime;
                    }

                    TimeSpan? duration = null;
                    if (item.TryGetProperty("duration", out var durEl))
                    {
                        var seconds = durEl.GetInt32();
                        if (seconds > 0)
                            duration = TimeSpan.FromSeconds(seconds);
                    }

                    long views = 0;
                    if (item.TryGetProperty("views", out var viewsEl))
                        views = viewsEl.GetInt64();

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Thumbnail = !string.IsNullOrEmpty(thumbnail) ? thumbnail : null,
                        IframeSrc = iframeSrc,
                        Duration = duration,
                        PublishedDate = publishedDate,
                        Views = views,
                        Author = !string.IsNullOrEmpty(uploader) ? uploader : null,
                        Engine = Name,
                        Category = SearchCategory.Videos,
                        Type = SearchResultType.Video,
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
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
