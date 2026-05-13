using SearXSharp.Models;
using System.Text.Json;
using System.Web;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Baidu (baidu.com).
/// Chinese search engine. Uses Baidu's JSON API.
/// Based on SearXNG's baidu.py.
/// </summary>
public class BaiduSearchEngine : SearchEngineBase
{
    private const string _endpoint = "https://www.baidu.com/s";

    private static readonly Dictionary<TimeRange, int> _timeRangeMap = new()
    {
        [TimeRange.Day] = 86400,
        [TimeRange.Week] = 604800,
        [TimeRange.Month] = 2592000,
        [TimeRange.Year] = 31536000,
    };

    /// <inheritdoc />
    public override string Name => "baidu";

    /// <inheritdoc />
    public override string DisplayName => "Baidu";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General, SearchCategory.Web };

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

    public BaiduSearchEngine() : base() { }
    public BaiduSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["wd"] = query.Query,
                ["rn"] = "10",
                ["pn"] = ((query.Page - 1) * 10).ToString(),
                ["tn"] = "json",
            };

            if (query.TimeRange.HasValue && _timeRangeMap.TryGetValue(query.TimeRange.Value, out var seconds))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var past = now - seconds;
                queryParams["gpc"] = $"stf={past},{now}|stftype=1";
            }

            var url = _endpoint + "?" + string.Join("&",
                queryParams.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseGeneralJson(json);
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

    private SearchResultList ParseGeneralJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("feed", out var feed)
                || !feed.TryGetProperty("entry", out var entries))
                return CreateResultList(results);

            foreach (var entry in entries.EnumerateArray())
            {
                try
                {
                    var title = entry.TryGetProperty("title", out var titleEl)
                        ? System.Net.WebUtility.HtmlDecode(titleEl.GetString() ?? "") : "";
                    var url = entry.TryGetProperty("url", out var urlEl)
                        ? urlEl.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url))
                        continue;

                    var content = entry.TryGetProperty("abs", out var absEl)
                        ? System.Net.WebUtility.HtmlDecode(absEl.GetString() ?? "") : "";

                    DateTime? publishedDate = null;
                    if (entry.TryGetProperty("time", out var timeEl))
                    {
                        var unixTime = timeEl.GetInt64();
                        publishedDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Web,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse entry", Name);
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
