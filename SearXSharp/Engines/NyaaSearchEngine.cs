using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Nyaa.si (anime bittorrent tracker).
/// Uses HTML scraping of Nyaa's search page, extracting torrent metadata.
/// Based on SearXNG's nyaa.py.
/// </summary>
public class NyaaSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://nyaa.si";

    /// <inheritdoc />
    public override string Name => "nyaa";

    /// <inheritdoc />
    public override string DisplayName => "Nyaa";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 50;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public NyaaSearchEngine() : base() { }
    public NyaaSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _baseUrl + "?q=" + Uri.EscapeDataString(query.Query) + "&p=" + query.Page;
            var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseHtml(html);
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

    private SearchResultList ParseHtml(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // XPath equivalent: //table[contains(@class, "torrent-list")]//tr[not(th)]
            var rows = document.QuerySelectorAll("table.torrent-list > tbody > tr, table.torrent-list > tr");

            foreach (var row in rows)
            {
                try
                {
                    // Skip header rows
                    if (row.QuerySelector("th") != null) continue;

                    // Category (td[1]/a)
                    var catLink = row.QuerySelector("td:nth-child(1) a");
                    var category = catLink?.GetAttribute("title")?.Trim() ?? "";

                    // Title (td[2]/a[last()])
                    var titleLinks = row.QuerySelectorAll("td:nth-child(2) a");
                    var titleLink = titleLinks.LastOrDefault();
                    if (titleLink == null) continue;

                    var title = titleLink.TextContent.Trim();
                    var href = titleLink.GetAttribute("href") ?? "";
                    var pageUrl = href.StartsWith("http") ? href : _baseUrl + href;

                    // Torrent links (td[3]/a) — magnet and torrent file
                    var downloadLinks = row.QuerySelectorAll("td:nth-child(3) a");
                    var magnetLink = "";
                    var torrentFile = "";
                    foreach (var link in downloadLinks)
                    {
                        var url = link.GetAttribute("href") ?? "";
                        if (url.Contains("magnet"))
                            magnetLink = url;
                        else
                            torrentFile = url.StartsWith("http") ? url : _baseUrl + url;
                    }

                    // Filesize (td[4])
                    var filesize = row.QuerySelector("td:nth-child(4)")?.TextContent?.Trim() ?? "";

                    // Date (td[5])
                    var dateStr = row.QuerySelector("td:nth-child(5)")?.TextContent?.Trim() ?? "";

                    // Seeds (td[6])
                    var seedStr = row.QuerySelector("td:nth-child(6)")?.TextContent?.Trim() ?? "0";
                    int.TryParse(seedStr, out var seeds);

                    // Leeches (td[7])
                    var leechStr = row.QuerySelector("td:nth-child(7)")?.TextContent?.Trim() ?? "0";
                    int.TryParse(leechStr, out var leeches);

                    // Downloads (td[8])
                    var downloadsStr = row.QuerySelector("td:nth-child(8)")?.TextContent?.Trim() ?? "0";
                    int.TryParse(downloadsStr, out var downloads);

                    var content = $"Category: \"{category}\". Downloaded {downloads} times. Size: {filesize}";

                    DateTime? publishedDate = null;
                    if (DateTime.TryParse(dateStr, out var dt))
                        publishedDate = dt;

                    var metadata = $"S:{seeds} L:{leeches} D:{downloads} | {filesize}";

                    results.Add(new SearchResult
                    {
                        Url = pageUrl,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Files,
                        Metadata = metadata,
                        // Store magnet/torrent in metadata-like way
                        // In a full implementation, we'd have dedicated fields
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse torrent row", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return CreateResultList(results);
    }
}
