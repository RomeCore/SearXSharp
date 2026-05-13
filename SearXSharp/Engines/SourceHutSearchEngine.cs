using AngleSharp;
using SearXSharp.Models;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for SourceHut (sr.ht) - the collaborative software platform.
/// Searches for public projects and repositories.
/// Based on SearXNG's sourcehut.py.
/// </summary>
public class SourceHutSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://sr.ht/projects";

    /// <inheritdoc />
    public override string Name => "sourcehut";

    /// <inheritdoc />
    public override string DisplayName => "SourceHut";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Repos };

    /// <inheritdoc />
    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public SourceHutSearchEngine() : base() { }
    public SourceHutSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["search"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["sort"] = "recently-updated",
            };

            var url = _baseUrl + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} SourceHut projects", Name, results.Count);
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

            var events = document.QuerySelectorAll("div.event-list > div.event");

            foreach (var item in events)
            {
                try
                {
                    var heading = item.QuerySelector("h4");
                    if (heading == null) continue;

                    var links = heading.QuerySelectorAll("a");
                    if (links.Length < 2) continue;

                    var maintainer = links[0].TextContent.Trim().TrimStart('~');
                    var projectLink = links[1];
                    var projectHref = projectLink.GetAttribute("href") ?? "";
                    var projectName = projectLink.TextContent.Trim();
                    var fullTitle = heading.TextContent.Trim();

                    var url = projectHref.StartsWith("http") ? projectHref : _baseUrl + projectHref;

                    var contentEl = item.QuerySelector("p");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    var tags = new List<string>();
                    var tagContainer = item.QuerySelector("div.tags");
                    if (tagContainer != null)
                    {
                        var tagLinks = tagContainer.QuerySelectorAll("a");
                        foreach (var tag in tagLinks)
                        {
                            var tagName = tag.TextContent.Trim().TrimStart('#');
                            if (!string.IsNullOrEmpty(tagName))
                                tags.Add(tagName);
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = fullTitle,
                        Content = content,
                        Author = maintainer,
                        Tags = tags,
                        Engine = Name,
                        Category = SearchCategory.Repos,
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
}
