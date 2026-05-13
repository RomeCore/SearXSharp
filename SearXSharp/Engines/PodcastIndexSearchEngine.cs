using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Podcast Index (podcastindex.org).
/// Podcast Index is an open, independent directory of podcasts.
/// Based on SearXNG's podcastindex.py.
/// </summary>
public class PodcastIndexSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://podcastindex.org";

    /// <inheritdoc />
    public override string Name => "podcastindex";

    /// <inheritdoc />
    public override string DisplayName => "Podcast Index";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Music, SearchCategory.General };

    /// <inheritdoc />
    public override bool SupportsPaging => false;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 1;
    public override double Timeout => 10.0;

    public PodcastIndexSearchEngine() : base() { }
    public PodcastIndexSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _baseUrl + "/api/search/byterm?q=" + Uri.EscapeDataString(query.Query);

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} podcast results", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");

            foreach (var feed in feeds.EnumerateArray())
            {
                var title = feed.GetProperty("title").GetString() ?? "";
                var description = feed.GetProperty("description").GetString() ?? "";
                var link = feed.GetProperty("link").GetString() ?? "";
                var image = feed.GetProperty("image").GetString() ?? "";
                var author = feed.GetProperty("author").GetString() ?? "";

                DateTime? publishedDate = null;
                if (feed.TryGetProperty("newestItemPubdate", out var pubDate))
                {
                    var unixTime = pubDate.GetInt64();
                    if (unixTime > 0)
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                }

                var episodeCount = 0;
                if (feed.TryGetProperty("episodeCount", out var epCount))
                    episodeCount = epCount.GetInt32();

                if (string.IsNullOrEmpty(title)) continue;

                results.Add(new SearchResult
                {
                    Url = link,
                    Title = title,
                    Content = description,
                    Thumbnail = image,
                    Author = author,
                    PublishedDate = publishedDate,
                    Metadata = $"{author}, {episodeCount} episodes",
                    Engine = Name,
                    Category = SearchCategory.Music,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return results;
    }
}
