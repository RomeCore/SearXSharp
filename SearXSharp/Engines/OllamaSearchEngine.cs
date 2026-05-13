using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Ollama (ollama.com).
/// Ollama is a platform for running large language models locally.
/// Searches for available models on the Ollama library.
/// Based on SearXNG's ollama.py.
/// </summary>
public class OllamaSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://ollama.com";

    /// <inheritdoc />
    public override string Name => "ollama";

    /// <inheritdoc />
    public override string DisplayName => "Ollama";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Repos };

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

    public OllamaSearchEngine() : base() { }
    public OllamaSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = _baseUrl + "/search?q=" + Uri.EscapeDataString(query.Query);

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} Ollama model results", Name, results.Count);
            return CreateResultList(results);
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

    private List<SearchResult> ParseHtml(string html)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            // Each model is in an <li> with x-test-model attribute
            var items = document.QuerySelectorAll("li[x-test-model]");

            foreach (var item in items)
            {
                try
                {
                    // Title
                    var titleEl = item.QuerySelector("span[x-test-search-response-title]");
                    var title = titleEl?.TextContent.Trim() ?? string.Empty;

                    if (string.IsNullOrEmpty(title)) continue;

                    // URL
                    var link = item.QuerySelector("a");
                    var href = link?.GetAttribute("href") ?? string.Empty;
                    var url = string.IsNullOrEmpty(href) ? "" : _baseUrl + href;

                    // Content / description
                    var descEl = item.QuerySelector("p");
                    var content = descEl?.TextContent.Trim() ?? string.Empty;

                    // Published date
                    DateTime? publishedDate = null;
                    var dateEl = item.QuerySelector("span[title]");
                    if (dateEl != null)
                    {
                        var dateStr = dateEl.GetAttribute("title");
                        if (!string.IsNullOrEmpty(dateStr))
                        {
                            // Format example: "Jan 15, 2025 03:22 AM UTC"
                            if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
                                publishedDate = dt;
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.IT,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse Ollama model item", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }
}
