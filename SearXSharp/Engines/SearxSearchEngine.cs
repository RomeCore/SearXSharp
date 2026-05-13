using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for SearXNG instances.
/// Queries another SearXNG instance via its JSON API.
/// Based on SearXNG's searx_engine.py.
/// </summary>
public class SearxSearchEngine : SearchEngineBase
{
    /// <summary>
    /// List of SearXNG instance URLs to query (e.g., "https://searx.example.com/search").
    /// </summary>
    public static List<string> InstanceUrls { get; set; } = new()
    {
        "https://searx.be/search",
    };

    private static int _instanceIndex = 0;

    /// <inheritdoc />
    public override string Name => "searxng";

    /// <inheritdoc />
    public override string DisplayName => "SearXNG";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web, SearchCategory.Images,
                  SearchCategory.Videos, SearchCategory.News, SearchCategory.Science,
                  SearchCategory.IT, SearchCategory.SocialMedia };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => true;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    public SearxSearchEngine() : base() { }
    public SearxSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            if (InstanceUrls.Count == 0)
                return CreateErrorResult("no_instances");

            var instanceUrl = InstanceUrls[_instanceIndex % InstanceUrls.Count];
            Interlocked.Increment(ref _instanceIndex);

            var formData = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["pageno"] = query.Page.ToString(),
                ["language"] = query.Language ?? "all",
                ["format"] = "json",
            };

            if (query.TimeRange.HasValue)
            {
                formData["time_range"] = query.TimeRange.Value switch
                {
                    TimeRange.Day => "day",
                    TimeRange.Week => "week",
                    TimeRange.Month => "month",
                    TimeRange.Year => "year",
                    _ => "",
                };
            }

            formData["category"] = query.Category switch
            {
                SearchCategory.General => "general",
                SearchCategory.Web => "general",
                SearchCategory.Images => "images",
                SearchCategory.Videos => "videos",
                SearchCategory.News => "news",
                SearchCategory.Science => "science",
                SearchCategory.IT => "it",
                SearchCategory.SocialMedia => "social media",
                _ => "general",
            };

            using var request = CreatePostRequest(instanceUrl, formData);
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
        var suggestions = new List<string>();
        var answers = new List<SearchAnswer>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    try
                    {
                        var url = item.TryGetProperty("url", out var urlEl)
                            ? urlEl.GetString() ?? "" : "";
                        var title = item.TryGetProperty("title", out var titleEl)
                            ? titleEl.GetString() ?? "" : "";
                        var content = item.TryGetProperty("content", out var contentEl)
                            ? contentEl.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(url)) continue;

                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = title,
                            Content = content,
                            Engine = Name,
                            Category = SearchCategory.Web,
                        });
                    }
                    catch { }
                }
            }

            if (root.TryGetProperty("answers", out var ans))
            {
                foreach (var a in ans.EnumerateArray())
                {
                    answers.Add(new SearchAnswer
                    {
                        Answer = a.TryGetProperty("answer", out var ansEl)
                            ? ansEl.GetString() ?? "" : "",
                    });
                }
            }

            if (root.TryGetProperty("suggestions", out var sugg))
            {
                foreach (var s in sugg.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.String)
                        suggestions.Add(s.GetString() ?? "");
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return new SearchResultList
        {
            Results = results,
            Suggestions = suggestions,
            Answers = answers,
        };
    }
}
