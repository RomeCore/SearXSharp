using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for FindThatMeme (findthatmeme.com).
/// Searches for memes by description.
/// Based on SearXNG's findthatmeme.py.
/// </summary>
public class FindThatMemeSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://findthatmeme.com/api/v1/search";

    /// <inheritdoc />
    public override string Name => "findthatmeme";

    /// <inheritdoc />
    public override string DisplayName => "FindThatMeme";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public FindThatMemeSearchEngine() : base() { }
    public FindThatMemeSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var startIndex = (query.Page - 1) * 50;
            var data = JsonSerializer.Serialize(new { search = query.Query, offset = startIndex });

            using var request = CreateJsonPostRequest(_baseUrl, data);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} meme results", Name, results.Count);
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
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var imagePath = item.GetProperty("image_path").GetString() ?? "";
                var thumbnail = item.TryGetProperty("thumbnail", out var t) ? t.GetString() : null;
                var sourcePage = item.GetProperty("source_page_url").GetString() ?? "";
                var sourceSite = item.GetProperty("source_site").GetString() ?? "";
                var itemType = item.GetProperty("type").GetString() ?? "";

                var imgSrc = "https://s3.thehackerblog.com/findthatmeme/" + imagePath;
                var thumbSrc = !string.IsNullOrEmpty(thumbnail)
                    ? "https://s3.thehackerblog.com/findthatmeme/thumb/" + thumbnail
                    : null;

                DateTime? publishedDate = null;
                if (item.TryGetProperty("updated_at", out var updated))
                {
                    var dateStr = updated.GetString() ?? "";
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr.Split('T')[0], out var dt))
                        publishedDate = dt;
                }

                results.Add(new SearchResult
                {
                    Url = sourcePage,
                    Title = sourceSite,
                    ImgSrc = itemType == "IMAGE" ? imgSrc : (thumbSrc ?? imgSrc),
                    Thumbnail = thumbSrc ?? imgSrc,
                    PublishedDate = publishedDate,
                    Engine = Name,
                    Type = SearchResultType.Image,
                    Category = SearchCategory.Images,
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
