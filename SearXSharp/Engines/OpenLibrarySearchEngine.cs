using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Open Library (openlibrary.org).
/// Uses search.json API (no API key required).
/// Based on SearXNG's openlibrary.py.
/// </summary>
public class OpenLibrarySearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://openlibrary.org";
    private const string _searchApi = "https://openlibrary.org/search.json";
    private const int _resultsPerPage = 10;

    /// <inheritdoc />
    public override string Name => "openlibrary";

    /// <inheritdoc />
    public override string DisplayName => "Open Library";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Science, SearchCategory.General };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 10;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    public OpenLibrarySearchEngine() : base() { }
    public OpenLibrarySearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["limit"] = _resultsPerPage.ToString(),
                ["fields"] = "*",
            };

            var url = _searchApi + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

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
            _logger.Error(ex, "{Engine}: Search failed for query: {Query}", Name, query.Query);
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

            if (!root.TryGetProperty("docs", out var docs))
                return CreateResultList(results);

            foreach (var item in docs.EnumerateArray())
            {
                try
                {
                    var key = item.GetProperty("key").GetString() ?? "";
                    var title = item.GetProperty("title").GetString() ?? "";

                    // Cover image
                    var thumbnail = "";
                    if (item.TryGetProperty("lending_identifier_s", out var lendingId))
                        thumbnail = $"https://archive.org/services/img/{lendingId.GetString()}";

                    // Authors
                    var authors = new List<string>();
                    if (item.TryGetProperty("author_name", out var authorNames))
                    {
                        foreach (var a in authorNames.EnumerateArray())
                        {
                            var aStr = a.GetString();
                            if (!string.IsNullOrEmpty(aStr))
                                authors.Add(aStr);
                        }
                    }

                    // First sentence as content
                    var content = "";
                    if (item.TryGetProperty("first_sentence", out var sentences))
                    {
                        var parts = sentences.EnumerateArray()
                            .Select(s => s.GetString())
                            .Where(s => !string.IsNullOrEmpty(s));
                        content = string.Join(" / ", parts);
                    }

                    // Published date
                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("publish_date", out var pubDates))
                    {
                        foreach (var d in pubDates.EnumerateArray())
                        {
                            var dStr = d.GetString();
                            if (!string.IsNullOrEmpty(dStr) && DateTime.TryParse(dStr, out var dt))
                            {
                                publishedDate = dt;
                                break;
                            }
                        }
                    }
                    if (publishedDate == null && item.TryGetProperty("first_publish_year", out var firstYear))
                    {
                        if (int.TryParse(firstYear.GetString(), out var year))
                            publishedDate = new DateTime(year, 1, 1);
                    }

                    // ISBNs
                    var isbns = new List<string>();
                    if (item.TryGetProperty("isbn", out var isbnEl))
                    {
                        foreach (var isbn in isbnEl.EnumerateArray().Take(5))
                        {
                            var isbnStr = isbn.GetString();
                            if (!string.IsNullOrEmpty(isbnStr))
                                isbns.Add(isbnStr);
                        }
                    }

                    // Tags (subjects + places)
                    var tags = new List<string>();
                    if (item.TryGetProperty("subject", out var subjects))
                    {
                        foreach (var s in subjects.EnumerateArray().Take(10))
                        {
                            var sStr = s.GetString();
                            if (!string.IsNullOrEmpty(sStr))
                                tags.Add(sStr);
                        }
                    }
                    if (item.TryGetProperty("place", out var places))
                    {
                        foreach (var p in places.EnumerateArray().Take(10))
                        {
                            var pStr = p.GetString();
                            if (!string.IsNullOrEmpty(pStr) && !tags.Contains(pStr))
                                tags.Add(pStr);
                        }
                    }

                    results.Add(new SearchResult
                    {
                        Url = $"{_baseUrl}/{key}",
                        Title = title,
                        Content = content,
                        Authors = authors,
                        PublishedDate = publishedDate,
                        Thumbnail = string.IsNullOrEmpty(thumbnail) ? null : thumbnail,
                        Tags = tags,
                        // Store ISBNs as metadata
                        Metadata = isbns.Count > 0 ? $"ISBN: {string.Join(", ", isbns)}" : null,
                        Engine = Name,
                        Category = SearchCategory.Science,
                        Type = SearchResultType.Paper,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse doc", Name);
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
