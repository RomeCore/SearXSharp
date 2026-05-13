using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for IMDb (imdb.com).
/// Uses undocumented suggestion API (no API key required).
/// Based on SearXNG's imdb.py.
/// </summary>
public class ImdbSearchEngine : SearchEngineBase
{
    private const string _suggestionUrl = "https://v2.sg.media-imdb.com/suggestion/{0}/{1}.json";
    private const string _hrefBase = "https://imdb.com/{0}/{1}";

    private static readonly Dictionary<string, string> _searchCategories = new()
    {
        ["nm"] = "name",
        ["tt"] = "title",
        ["kw"] = "keyword",
        ["co"] = "company",
        ["ep"] = "episode",
    };

    /// <inheritdoc />
    public override string Name => "imdb";

    /// <inheritdoc />
    public override string DisplayName => "IMDb";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files, SearchCategory.Music };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public ImdbSearchEngine() : base() { }
    public ImdbSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var q = query.Query.Replace(" ", "_").ToLowerInvariant();
            var url = string.Format(_suggestionUrl, q[0], q);

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

            if (!root.TryGetProperty("d", out var entries))
                return CreateResultList(results);

            foreach (var entry in entries.EnumerateArray())
            {
                try
                {
                    var entryId = entry.GetProperty("id").GetString() ?? "";
                    var prefix = entryId.Length >= 2 ? entryId[..2] : "";

                    if (!_searchCategories.TryGetValue(prefix, out var category))
                        continue;

                    var title = entry.GetProperty("l").GetString() ?? "";

                    // Add qualifier
                    if (entry.TryGetProperty("q", out var qEl))
                        title += $" ({qEl.GetString()})";

                    // Build content
                    var contentParts = new List<string>();
                    if (entry.TryGetProperty("rank", out var rank))
                        contentParts.Add($"({rank.GetString()})");
                    if (entry.TryGetProperty("y", out var year))
                        contentParts.Add($"{year.GetString()} - ");
                    if (entry.TryGetProperty("s", out var stars))
                        contentParts.Add(stars.GetString() ?? "");
                    var content = string.Join(" ", contentParts);

                    // Image handling
                    var thumbnail = "";
                    if (entry.TryGetProperty("i", out var imgObj)
                        && imgObj.TryGetProperty("imageUrl", out var imgUrlEl))
                    {
                        var imageUrl = imgUrlEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            var lastDot = imageUrl.LastIndexOf('.');
                            if (lastDot >= 0)
                            {
                                var name = imageUrl[..lastDot];
                                var ext = imageUrl[(lastDot + 1)..];
                                var magic = "QL75_UX280_CR0,0,280,414_";
                                if (!name.EndsWith("_V1_"))
                                    magic = "_V1_" + magic;
                                thumbnail = name + magic + "." + ext;
                            }
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = string.Format(_hrefBase, category, entryId),
                        Title = title,
                        Content = content,
                        Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                        Engine = Name,
                        Category = SearchCategory.Files,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse entry", Name);
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
