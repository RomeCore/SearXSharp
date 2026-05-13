using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Semantic Scholar (academic papers).
/// Uses Semantic Scholar's internal API (/api/1/search) with UI version detection.
/// Based on SearXNG's semantic_scholar.py.
/// </summary>
public class SemanticScholarSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://www.semanticscholar.org/api/1/search";
    private const string _baseUrl = "https://www.semanticscholar.org";

    // Simple in-memory cache for UI version
    private static string? _cachedUiVersion;
    private static DateTime _uiVersionExpiry = DateTime.MinValue;

    /// <inheritdoc />
    public override string Name => "semanticscholar";

    /// <inheritdoc />
    public override string DisplayName => "Semantic Scholar";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Science };

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

    public SemanticScholarSearchEngine() : base() { }
    public SemanticScholarSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var uiVersion = await GetUiVersionAsync(ct);

            var payload = JsonSerializer.Serialize(new
            {
                queryString = query.Query,
                page = query.Page,
                pageSize = 10,
                sort = "relevance",
                getQuerySuggestions = false,
                authors = new string[] { },
                coAuthors = new string[] { },
                venues = new string[] { },
                performTitleMatch = true,
            });

            var request = CreateJsonPostRequest(_searchUrl, payload);
            request.Headers.TryAddWithoutValidation("X-S2-UI-Version", uiVersion);
            request.Headers.TryAddWithoutValidation("X-S2-Client", "webapp-browser");
            request.Headers.TryAddWithoutValidation("Origin", _baseUrl);
            request.Headers.TryAddWithoutValidation("Referer", $"{_baseUrl}/");

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

    /// <summary>
    /// Gets the Semantic Scholar UI version from the main page (cached for 5 min).
    /// SearXNG does this to bypass API version detection.
    /// </summary>
    private async Task<string> GetUiVersionAsync(CancellationToken ct)
    {
        if (_cachedUiVersion != null && DateTime.UtcNow < _uiVersionExpiry)
            return _cachedUiVersion;

        var request = CreateGetRequest(_baseUrl);
        var response = await SendRequestAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);

        // Extract X-S2-UI-Version from meta tag
        const string metaPattern = "<meta name=\"s2-ui-version\" content=\"";
        var start = html.IndexOf(metaPattern, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += metaPattern.Length;
            var end = html.IndexOf("\"", start, StringComparison.Ordinal);
            if (end > start)
            {
                _cachedUiVersion = html[start..end];
                _uiVersionExpiry = DateTime.UtcNow.AddMinutes(5);
                return _cachedUiVersion;
            }
        }

        // Fallback to a default
        _cachedUiVersion = "4";
        _uiVersionExpiry = DateTime.UtcNow.AddMinutes(1);
        return _cachedUiVersion;
    }

    private SearchResultList ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var data))
                return CreateResultList(results);

            foreach (var result in data.EnumerateArray())
            {
                try
                {
                    // Extract title
                    var title = "";
                    if (result.TryGetProperty("title", out var titleEl)
                        && titleEl.TryGetProperty("text", out var titleText))
                        title = titleText.GetString() ?? "";

                    // Extract URL
                    var url = "";
                    if (result.TryGetProperty("primaryPaperLink", out var primaryLink)
                        && primaryLink.TryGetProperty("url", out var primaryUrl))
                        url = primaryUrl.GetString() ?? "";

                    if (string.IsNullOrEmpty(url) && result.TryGetProperty("links", out var links))
                    {
                        foreach (var link in links.EnumerateArray())
                        {
                            url = link.GetString() ?? "";
                            if (!string.IsNullOrEmpty(url)) break;
                        }
                    }

                    if (string.IsNullOrEmpty(url) && result.TryGetProperty("alternatePaperLinks", out var altLinks))
                    {
                        foreach (var alt in altLinks.EnumerateArray())
                        {
                            if (alt.TryGetProperty("url", out var altUrl))
                            {
                                url = altUrl.GetString() ?? "";
                                if (!string.IsNullOrEmpty(url)) break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(url))
                    {
                        var paperId = result.GetProperty("id").GetString() ?? "";
                        url = $"{_baseUrl}/paper/{paperId}";
                    }

                    // Abstract
                    var abstractText = "";
                    if (result.TryGetProperty("paperAbstract", out var abs)
                        && abs.TryGetProperty("text", out var absText))
                        abstractText = absText.GetString() ?? "";

                    // Authors
                    var authors = new List<string>();
                    if (result.TryGetProperty("authors", out var authorsEl))
                    {
                        foreach (var author in authorsEl.EnumerateArray())
                        {
                            if (author.ValueKind == JsonValueKind.Array && author.GetArrayLength() > 0)
                            {
                                var name = author[0].GetProperty("name").GetString() ?? "";
                                if (!string.IsNullOrEmpty(name))
                                    authors.Add(name);
                            }
                        }
                    }

                    // Published date
                    DateTime? publishedDate = null;
                    if (result.TryGetProperty("pubDate", out var pubDate))
                    {
                        if (DateTime.TryParse(pubDate.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    // Venue / Journal
                    var venue = "";
                    if (result.TryGetProperty("venue", out var venueEl)
                        && venueEl.TryGetProperty("text", out var venueText))
                        venue = venueText.GetString() ?? "";

                    var journal = "";
                    if (result.TryGetProperty("journal", out var journalEl)
                        && journalEl.TryGetProperty("name", out var journalName))
                        journal = journalName.GetString() ?? "";
                    if (string.IsNullOrEmpty(journal)) journal = venue;

                    // DOI
                    var doi = "";
                    if (result.TryGetProperty("doiInfo", out var doiInfo)
                        && doiInfo.TryGetProperty("doi", out var doiEl))
                        doi = doiEl.GetString() ?? "";

                    // Fields of study (tags)
                    var tags = new List<string>();
                    if (result.TryGetProperty("fieldsOfStudy", out var fields))
                    {
                        foreach (var field in fields.EnumerateArray())
                            tags.Add(field.GetString() ?? "");
                    }

                    // PDF URL
                    var pdfUrl = "";
                    if (result.TryGetProperty("alternatePaperLinks", out var altPaperLinks))
                    {
                        foreach (var alt in altPaperLinks.EnumerateArray())
                        {
                            var linkType = alt.GetProperty("linkType").GetString() ?? "";
                            if (linkType != "crawler" && linkType != "doi")
                            {
                                if (alt.TryGetProperty("url", out var pdfLink))
                                {
                                    pdfUrl = pdfLink.GetString() ?? "";
                                    break;
                                }
                            }
                        }
                    }

                    // Citation info
                    var comments = "";
                    if (result.TryGetProperty("citationStats", out var cs))
                    {
                        var numCitations = cs.GetProperty("numCitations").GetInt32();
                        var firstYear = cs.GetProperty("firstCitationVelocityYear").GetInt32();
                        var lastYear = cs.GetProperty("lastCitationVelocityYear").GetInt32();
                        comments = $"{numCitations} citations ({firstYear}-{lastYear})";
                    }

                    if (abstractText.Length > 500)
                        abstractText = abstractText[..500] + "...";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = abstractText,
                        PublishedDate = publishedDate,
                        Authors = authors,
                        Journal = journal,
                        Doi = doi,
                        PdfUrl = pdfUrl,
                        Tags = tags,
                        Comments = comments,
                        Engine = Name,
                        Category = SearchCategory.Science,
                        Type = SearchResultType.Paper,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse paper", Name);
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
