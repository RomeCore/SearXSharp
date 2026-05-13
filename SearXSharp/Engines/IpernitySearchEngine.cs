using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Ipernity (ipernity.com) - photo sharing platform.
/// Based on SearXNG's ipernity.py.
/// </summary>
public partial class IpernitySearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.ipernity.com";
    private const int _pageSize = 10;

    /// <inheritdoc />
    public override string Name => "ipernity";

    /// <inheritdoc />
    public override string DisplayName => "Ipernity";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public IpernitySearchEngine() : base() { }
    public IpernitySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _baseUrl + $"/search/photo/@/page:{query.Page}:{_pageSize}?q={Uri.EscapeDataString(query.Query)}";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} Ipernity results", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseHtml(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Get all image elements in links starting with /doc
            var images = document.QuerySelectorAll("a[href^='/doc'] img");

            // Get all script tags with JSON data
            var scripts = document.QuerySelectorAll("script[type='text/javascript']");
            var jsonRegex = JsDataRegex();

            var imageIndex = 0;
            foreach (var script in scripts)
            {
                var text = script.TextContent;
                var match = jsonRegex.Match(text);
                if (!match.Success) continue;

                var jsonStr = match.Groups[1].Value + "}";
                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("mediakey", out _)) continue;

                    var medakey = root.GetProperty("mediakey").GetString() ?? "";
                    var userId = root.GetProperty("user_id").GetString() ?? "";
                    var docId = root.GetProperty("doc_id").GetString() ?? "";
                    var title = root.GetProperty("title").GetString() ?? "";
                    var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                    var width = root.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    var height = root.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    var resolution = width > 0 && height > 0 ? $"{width}x{height}" : null;

                    DateTime? publishedDate = null;
                    if (root.TryGetProperty("posted_at", out var posted))
                    {
                        var unixTime = posted.GetInt64();
                        if (unixTime > 0)
                            publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }

                    string? thumbnailSrc = null;
                    string? imgSrc = null;

                    if (imageIndex < images.Length)
                    {
                        thumbnailSrc = images[imageIndex].GetAttribute("src");
                        if (!string.IsNullOrEmpty(thumbnailSrc))
                            imgSrc = thumbnailSrc.Replace("240.jpg", "640.jpg");
                    }
                    imageIndex++;

                    results.Add(new SearchResult
                    {
                        Url = $"{_baseUrl}/doc/{userId}/{docId}",
                        Title = title,
                        Content = content,
                        Thumbnail = thumbnailSrc,
                        ImgSrc = imgSrc,
                        Resolution = resolution,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Type = SearchResultType.Image,
                        Category = SearchCategory.Images,
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }

    [GeneratedRegex(@"\]\s*=\s*(\{.*?\});?\s*$", RegexOptions.Multiline)]
    private static partial Regex JsDataRegex();
}
