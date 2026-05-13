using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Mixcloud (music & podcast streaming).
/// Uses Mixcloud's public API (no key required).
/// Based on SearXNG's mixcloud.py.
/// </summary>
public class MixcloudSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.mixcloud.com/search/";
    private const string _iframePattern = "https://www.mixcloud.com/widget/iframe/?feed={0}";

    /// <inheritdoc />
    public override string Name => "mixcloud";

    /// <inheritdoc />
    public override string DisplayName => "Mixcloud";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Music };

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

    public MixcloudSearchEngine() : base() { }
    public MixcloudSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var offset = (query.Page - 1) * 10;
            var url = _searchUrl + "?" + string.Join("&", new[]
            {
                "q=" + Uri.EscapeDataString(query.Query),
                "type=cloudcast",
                "limit=10",
                "offset=" + offset,
            });

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

            if (!root.TryGetProperty("data", out var data))
                return CreateResultList(results);

            foreach (var result in data.EnumerateArray())
            {
                try
                {
                    var rUrl = result.GetProperty("url").GetString() ?? "";
                    var name = result.GetProperty("name").GetString() ?? "";
                    var userName = result.GetProperty("user").GetProperty("name").GetString() ?? "";

                    var thumbnail = "";
                    if (result.TryGetProperty("pictures", out var pics)
                        && pics.TryGetProperty("medium", out var thumb))
                        thumbnail = thumb.GetString() ?? "";

                    DateTime? publishedDate = null;
                    if (result.TryGetProperty("created_time", out var created))
                    {
                        if (DateTime.TryParse(created.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = rUrl,
                        Title = name,
                        Content = userName,
                        Thumbnail = thumbnail,
                        IframeSrc = string.Format(_iframePattern, rUrl),
                        PublishedDate = publishedDate,
                        Author = userName,
                        Engine = Name,
                        Category = SearchCategory.Music,
                        Type = SearchResultType.Default,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse cloudcast", Name);
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
