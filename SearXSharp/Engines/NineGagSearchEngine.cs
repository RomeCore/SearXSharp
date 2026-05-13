using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for 9GAG (social media memes).
/// Uses 9GAG's internal search API (no key required).
/// Based on SearXNG's 9gag.py.
/// </summary>
public class NineGagSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://9gag.com/v1/search-posts";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "9gag";

    /// <inheritdoc />
    public override string DisplayName => "9GAG";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.SocialMedia, SearchCategory.Images };

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

    public NineGagSearchEngine() : base() { }
    public NineGagSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var offset = (query.Page - 1) * _pageSize;
            var url = $"{_searchUrl}?query={Uri.EscapeDataString(query.Query)}&c={offset}";

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

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("posts", out var posts))
                return CreateResultList(results);

            foreach (var post in posts.EnumerateArray())
            {
                try
                {
                    var type = post.GetProperty("type").GetString() ?? "";
                    var url = post.GetProperty("url").GetString() ?? "";
                    var title = post.GetProperty("title").GetString() ?? "";
                    var description = "";
                    if (post.TryGetProperty("description", out var desc))
                        description = desc.GetString() ?? "";

                    var images = post.GetProperty("images");
                    var image700 = images.GetProperty("image700");

                    var imgSrc = image700.GetProperty("url").GetString() ?? "";
                    var imgHeight = image700.GetProperty("height").GetInt32();

                    // Get the not cropped version when the image height is not too important
                    string thumbnail;
                    if (imgHeight > 400)
                        thumbnail = images.GetProperty("imageFbThumbnail").GetProperty("url").GetString() ?? "";
                    else
                        thumbnail = imgSrc;

                    DateTime? publishedDate = null;
                    if (post.TryGetProperty("creationTs", out var ts))
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64()).DateTime;

                    if (type == "Photo")
                    {
                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = description,
                            ImgSrc = imgSrc,
                            Thumbnail = thumbnail,
                            PublishedDate = publishedDate,
                            Engine = Name,
                            Category = SearchCategory.SocialMedia,
                            Type = SearchResultType.Image,
                        });
                    }
                    else if (type == "Animated")
                    {
                        var iframeSrc = "";
                        if (images.TryGetProperty("image460sv", out var animated))
                            iframeSrc = animated.GetProperty("url").GetString() ?? "";

                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = description,
                            Thumbnail = thumbnail,
                            IframeSrc = iframeSrc,
                            PublishedDate = publishedDate,
                            Engine = Name,
                            Category = SearchCategory.SocialMedia,
                            Type = SearchResultType.Video,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse post", Name);
                }
            }

            // Add tag suggestions if available
            if (data.TryGetProperty("tags", out var tags))
            {
                var suggestions = new List<string>();
                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.TryGetProperty("key", out var key))
                        suggestions.Add(key.GetString() ?? "");
                }
                // Suggestions would be nice but SearchResultList doesn't have them in constructor
                // They're set via init
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
