using AngleSharp;
using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Ask.com (www.ask.com).
/// Parses JSON embedded in the page's JavaScript.
/// Based on SearXNG's ask.py.
/// </summary>
public class AskSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.ask.com/web";

    /// <inheritdoc />
    public override string Name => "ask";

    /// <inheritdoc />
    public override string DisplayName => "Ask.com";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 5;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public AskSearchEngine() : base() { }
    public AskSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _baseUrl + $"?q={Uri.EscapeDataString(query.Query)}&page={query.Page}";

            using var request = CreateGetRequest(url);
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
            var startTag = "window.MESON.initialState = {";
            var endTag = "};";

            var startIdx = html.IndexOf(startTag);
            if (startIdx < 0) return CreateResultList(results);
            startIdx += startTag.Length - 1;

            var endIdx = html.IndexOf(endTag, startIdx);
            if (endIdx < 0) return CreateResultList(results);
            endIdx += endTag.Length - 1;

            var json = html[startIdx..endIdx];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("search", out var search)
                || !search.TryGetProperty("webResults", out var webResults)
                || !webResults.TryGetProperty("results", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var url = item.GetProperty("url").GetString() ?? "";
                    // Clean tracking params
                    var cleanUrl = url.Split("&ueid")[0];

                    var title = item.GetProperty("title").GetString() ?? "";
                    var content = item.TryGetProperty("abstract", out var absEl)
                        ? absEl.GetString() ?? "" : "";

                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("pubdate_original", out var pubEl))
                    {
                        var pubStr = pubEl.GetString();
                        if (!string.IsNullOrEmpty(pubStr) && DateTime.TryParse(pubStr, out var dt))
                            publishedDate = dt;
                    }

                    var metadataParts = new List<string>();
                    foreach (var field in new[] { "category_l1", "catsy" })
                    {
                        if (item.TryGetProperty(field, out var catEl) && catEl.ValueKind == JsonValueKind.String)
                            metadataParts.Add(catEl.GetString() ?? "");
                    }

                    results.Add(new SearchResult
                    {
                        Url = cleanUrl,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Metadata = string.Join(" | ", metadataParts),
                        Engine = Name,
                        Category = SearchCategory.Web,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse result item", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML/JSON", Name);
        }

        return CreateResultList(results);
    }
}
