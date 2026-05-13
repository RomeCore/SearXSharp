using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine for Kickass Torrents (kickasstorrents.to).
/// Searches for torrent files across various categories.
/// Based on SearXNG's kickass.py.
/// </summary>
public class KickassSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://kickasstorrents.to";

    /// <inheritdoc />
    public override string Name => "kickass";

    /// <inheritdoc />
    public override string DisplayName => "Kickass Torrents";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Files };

    /// <inheritdoc />
    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 15.0;

    public KickassSearchEngine() : base() { }
    public KickassSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _baseUrl + $"/usearch/{Uri.EscapeDataString(query.Query)}/{query.Page}/";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} torrent results", Name, results.Count);
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

            var rows = document.QuerySelectorAll("table.data tr:has(a)");

            foreach (var row in rows.Skip(1)) // Skip header
            {
                try
                {
                    var link = row.QuerySelector("a.cellMainLink");
                    if (link == null) continue;

                    var href = link.GetAttribute("href");
                    var title = link.TextContent.Trim();
                    if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(title)) continue;

                    var url = _baseUrl + href;

                    var contentEl = row.QuerySelector("span.font11px.lightgrey.block");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    var seedEl = row.QuerySelector("td.green");
                    var seed = int.TryParse(seedEl?.TextContent.Trim(), out var s) ? s : 0;

                    var leechEl = row.QuerySelector("td.red");
                    var leech = int.TryParse(leechEl?.TextContent.Trim(), out var l) ? l : 0;

                    var sizeEl = row.QuerySelector("td.nobr");
                    var size = sizeEl?.TextContent.Trim() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        Score = seed,
                        Metadata = $"S:{seed} L:{leech} Size:{size}",
                        Engine = Name,
                        Category = SearchCategory.Files,
                    });
                }
                catch { }
            }

            // Sort by seed count descending
            results = results.OrderByDescending(r => r.Score).ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }
}
