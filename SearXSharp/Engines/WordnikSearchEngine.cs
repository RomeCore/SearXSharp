using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Wordnik (wordnik.com).
/// Wordnik is an online English dictionary and language resource.
/// Based on SearXNG's wordnik.py.
/// </summary>
public partial class WordnikSearchEngine : SearchEngineBase
{
    /// <inheritdoc />
    public override string Name => "wordnik";

    /// <inheritdoc />
    public override string DisplayName => "Wordnik";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => false;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 1;
    public override double Timeout => 10.0;

    public WordnikSearchEngine() : base() { }
    public WordnikSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://www.wordnik.com/words/{Uri.EscapeDataString(query.Query)}";

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html, url);

            _logger.Debug("{Engine}: Parsed definitions for '{Query}'", Name, query.Query);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseHtml(string html, string pageUrl)
    {
        var results = new List<SearchResult>();

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();

            var sources = document.QuerySelectorAll("#define h3.source");

            foreach (var source in sources)
            {
                var sourceName = source.TextContent.Trim();
                var defList = source.NextElementSibling;
                if (defList == null || defList.TagName != "UL") continue;

                var definitions = new List<string>();
                foreach (var defItem in defList.QuerySelectorAll("li"))
                {
                    var abbr = defItem.QuerySelector("abbr");
                    var defText = defItem.TextContent.Trim();
                    if (abbr != null)
                        defText = defText.Replace(abbr.TextContent, "").Trim();
                    definitions.Add(defText);
                }

                var summary = definitions.FirstOrDefault() ?? "";
                var content = string.Join(" | ", definitions.Take(3));

                if (!string.IsNullOrEmpty(summary))
                {
                    results.Add(new SearchResult
                    {
                        Url = pageUrl,
                        Title = $"{query.Query} - {sourceName}",
                        Content = content,
                        Source = sourceName,
                        Engine = Name,
                        Category = SearchCategory.General,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }

    private string _query = "";
    private string query
    {
        get => _query;
        set => _query = value;
    }
}
