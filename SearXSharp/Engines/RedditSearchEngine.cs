using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Reddit.
/// Uses Reddit's official JSON API.
/// Based on SearXNG's reddit.py.
/// </summary>
public class RedditSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://www.reddit.com/search.json";

    /// <inheritdoc />
    public override string Name => "reddit";

    /// <inheritdoc />
    public override string DisplayName => "Reddit";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.SocialMedia, SearchCategory.Web };

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

    public RedditSearchEngine() : base() { }
    public RedditSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _searchUrl + "?q=" + Uri.EscapeDataString(query.Query)
                + "&limit=25";

            var request = CreateGetRequest(url);
            // Reddit API requires a User-Agent
            request.Headers.UserAgent.TryParseAdd(GetRandomUserAgent());

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
        var imgResults = new List<SearchResult>();
        var textResults = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("children", out var children))
                return CreateResultList(textResults);

            foreach (var child in children.EnumerateArray())
            {
                try
                {
                    var post = child.GetProperty("data");
                    var title = post.GetProperty("title").GetString() ?? "";
                    var permalink = post.GetProperty("permalink").GetString() ?? "";
                    var url = $"https://www.reddit.com{permalink}";

                    var thumbnail = "";
                    if (post.TryGetProperty("thumbnail", out var thumbEl))
                        thumbnail = thumbEl.GetString() ?? "";

                    var hasValidThumbnail = Uri.TryCreate(thumbnail, UriKind.Absolute, out var thumbUri)
                        && !string.IsNullOrEmpty(thumbUri.Host)
                        && !string.IsNullOrEmpty(thumbUri.AbsolutePath);

                    if (hasValidThumbnail)
                    {
                        var imgUrl = "";
                        if (post.TryGetProperty("url", out var imgUrlEl))
                            imgUrl = imgUrlEl.GetString() ?? "";

                        imgResults.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            ImgSrc = imgUrl,
                            Thumbnail = thumbnail,
                            Engine = Name,
                            Category = SearchCategory.SocialMedia,
                            Type = SearchResultType.Image,
                        });
                    }
                    else
                    {
                        var selfText = "";
                        if (post.TryGetProperty("selftext", out var selfTextEl))
                            selfText = selfTextEl.GetString() ?? "";

                        if (selfText.Length > 500)
                            selfText = selfText[..500] + "...";

                        DateTime? created = null;
                        if (post.TryGetProperty("created_utc", out var createdEl))
                        {
                            var unixTime = createdEl.GetDouble();
                            created = DateTimeOffset.FromUnixTimeSeconds((long)unixTime).DateTime;
                        }

                        textResults.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = selfText,
                            PublishedDate = created,
                            Engine = Name,
                            Category = SearchCategory.SocialMedia,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse post", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        // Return images first, then text results
        return new SearchResultList
        {
            Results = imgResults.Concat(textResults).ToList(),
        };
    }
}
