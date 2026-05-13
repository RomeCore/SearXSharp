using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for BPB (bpb.de) - Bundeszentrale für politische Bildung.
/// German governmental institution providing resources about politics and history.
/// Based on SearXNG's bpb.py.
/// </summary>
public class BpbSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.bpb.de";

    /// <inheritdoc />
    public override string Name => "bpb";

    /// <inheritdoc />
    public override string DisplayName => "BPB";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public BpbSearchEngine() : base() { }
    public BpbSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["query[term]"] = query.Query,
                ["page"] = (query.Page - 1).ToString(),
                ["sort[direction]"] = "descending",
                ["payload[nid]"] = "65350",
            };

            var url = _baseUrl + "/bpbapi/filter/search?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} BPB results", Name, results.Count);
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
            var teasers = doc.RootElement.GetProperty("teaser");

            foreach (var result in teasers.EnumerateArray())
            {
                var teaser = result.GetProperty("teaser");
                var extension = result.GetProperty("extension");

                var title = teaser.GetProperty("title").GetString() ?? "";
                var text = teaser.GetProperty("text").GetString() ?? "";

                var linkUrl = teaser.GetProperty("link").GetProperty("url").GetString() ?? "";
                var url = _baseUrl + linkUrl;

                // Thumbnail
                string? thumbnail = null;
                if (teaser.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
                {
                    var sources = image.GetProperty("sources");
                    if (sources.GetArrayLength() > 0)
                    {
                        var lastSrc = sources[sources.GetArrayLength() - 1];
                        thumbnail = _baseUrl + lastSrc.GetProperty("url").GetString();
                    }
                }

                // Metadata
                var overline = extension.GetProperty("overline").GetString() ?? "";

                var authors = new List<string>();
                if (extension.TryGetProperty("authors", out var authorsEl))
                {
                    foreach (var author in authorsEl.EnumerateArray())
                        authors.Add(author.GetProperty("name").GetString() ?? "");
                }

                var metadata = overline;
                if (authors.Count > 0)
                    metadata += " | " + string.Join(", ", authors);

                // Published date
                DateTime? publishedDate = null;
                if (extension.TryGetProperty("publishingDate", out var pubDate))
                {
                    var unixTime = pubDate.GetInt64();
                    if (unixTime > 0)
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                }

                results.Add(new SearchResult
                {
                    Url = url,
                    Title = title,
                    Content = text,
                    Thumbnail = thumbnail,
                    PublishedDate = publishedDate,
                    Author = authors.Count > 0 ? string.Join(", ", authors) : null,
                    Metadata = metadata,
                    Engine = Name,
                    Category = SearchCategory.General,
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
