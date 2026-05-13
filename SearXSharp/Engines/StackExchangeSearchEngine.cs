using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Stack Exchange / Stack Overflow.
/// Uses the official Stack Exchange API v2.3.
/// Based on SearXNG's stackexchange.py.
/// </summary>
public class StackExchangeSearchEngine : SearchEngineBase
{
    private const string _searchApi = "https://api.stackexchange.com/2.3/search/advanced?";
    private const string _apiSite = "stackoverflow";

    /// <inheritdoc />
    public override string Name => "stackoverflow";

    /// <inheritdoc />
    public override string DisplayName => "Stack Overflow";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Web };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public StackExchangeSearchEngine() : base() { }
    public StackExchangeSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["pagesize"] = "10",
                ["site"] = _apiSite,
                ["sort"] = "activity",
                ["order"] = "desc",
            };

            var url = _searchApi + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
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
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            if (!root.TryGetProperty("items", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var title = System.Net.WebUtility.HtmlDecode(item.GetProperty("title").GetString() ?? "");
                    var questionId = item.GetProperty("question_id").GetInt32();

                    var tags = new List<string>();
                    if (item.TryGetProperty("tags", out var tagsEl))
                        tags = tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").ToList();

                    var displayName = "";
                    if (item.TryGetProperty("owner", out var owner)
                        && owner.TryGetProperty("display_name", out var dn))
                        displayName = dn.GetString() ?? "";

                    var isAnswered = false;
                    if (item.TryGetProperty("is_answered", out var answered))
                        isAnswered = answered.GetBoolean();

                    var score = 0;
                    if (item.TryGetProperty("score", out var scoreEl))
                        score = scoreEl.GetInt32();

                    var content = $"[{string.Join(", ", tags)}] {displayName}";
                    if (isAnswered) content += " // is answered";
                    content += $" // score: {score}";

                    results.Add(new SearchResult
                    {
                        Url = $"https://{_apiSite}.com/q/{questionId}",
                        Title = title,
                        Content = content,
                        Tags = tags,
                        Score = score,
                        Author = displayName,
                        Engine = Name,
                        Category = SearchCategory.IT,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse item", Name);
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
