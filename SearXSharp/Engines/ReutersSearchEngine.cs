using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Reuters (reuters.com).
/// Uses the Reuters internal API (JSON).
/// Based on SearXNG's reuters.py.
/// </summary>
public class ReutersSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.reuters.com";

    private static readonly Dictionary<TimeRange, int> _timeRangeMap = new()
    {
        [TimeRange.Day] = 1,
        [TimeRange.Week] = 7,
        [TimeRange.Month] = 30,
        [TimeRange.Year] = 365,
    };

    /// <inheritdoc />
    public override string Name => "reuters";

    /// <inheritdoc />
    public override string DisplayName => "Reuters";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.News };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 12.0;

    public ReutersSearchEngine() : base() { }
    public ReutersSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var args = new Dictionary<string, object>
            {
                ["keyword"] = query.Query,
                ["offset"] = (query.Page - 1) * 20,
                ["orderby"] = "relevance",
                ["size"] = 20,
                ["website"] = "reuters",
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var days))
            {
                var startDate = DateTime.UtcNow.AddDays(-days);
                args["start_date"] = startDate.ToString("o");
            }

            var jsonArgs = JsonSerializer.Serialize(args);
            var url = $"{_baseUrl}/pf/api/v3/content/fetch/articles-by-search-v2?query={Uri.EscapeDataString(jsonArgs)}";

            using var request = CreateGetRequest(url);
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

            if (!root.TryGetProperty("result", out var result)
                || !result.TryGetProperty("articles", out var articles))
                return CreateResultList(results);

            foreach (var article in articles.EnumerateArray())
            {
                try
                {
                    var canonicalUrl = article.GetProperty("canonical_url").GetString() ?? "";
                    var url = _baseUrl + canonicalUrl;

                    var title = article.TryGetProperty("web", out var webEl)
                        ? webEl.GetString() ?? "" : "";
                    var description = article.TryGetProperty("description", out var descEl)
                        ? descEl.GetString() ?? "" : "";

                    var metadata = article.TryGetProperty("kicker", out var kicker)
                        && kicker.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() ?? "" : "";

                    DateTime? publishedDate = null;
                    if (article.TryGetProperty("display_time", out var dtEl))
                    {
                        if (DateTime.TryParse(dtEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    string? thumbnail = null;
                    if (article.TryGetProperty("thumbnail", out var thumbEl))
                    {
                        var resizerUrl = thumbEl.TryGetProperty("resizer_url", out var resEl)
                            ? resEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(resizerUrl))
                            thumbnail = resizerUrl + "&height=80";
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = description,
                        Thumbnail = thumbnail,
                        PublishedDate = publishedDate,
                        Metadata = metadata,
                        Engine = Name,
                        Category = SearchCategory.News,
                        Type = SearchResultType.News,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse article", Name);
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
