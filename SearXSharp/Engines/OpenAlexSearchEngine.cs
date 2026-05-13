using SearXSharp.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for OpenAlex (openalex.org).
/// OpenAlex provides a free, open API for scholarly research metadata.
/// Based on SearXNG's openalex.py.
/// </summary>
public class OpenAlexSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://api.openalex.org/works";

    /// <inheritdoc />
    public override string Name => "openalex";

    /// <inheritdoc />
    public override string DisplayName => "OpenAlex";

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

    public OpenAlexSearchEngine() : base() { }
    public OpenAlexSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["search"] = query.Query,
                ["page"] = query.Page.ToString(),
                ["per-page"] = "10",
                ["sort"] = "relevance_score:desc",
            };

            // Language filter (ISO 2-letter code)
            if (!string.IsNullOrEmpty(query.Language) && query.Language != "all" && query.Language.Length >= 2)
            {
                var iso2 = query.Language.Split('-')[0].Split('_')[0];
                if (iso2.Length == 2)
                    args["filter"] = $"language:{iso2}";
            }

            var url = _searchUrl + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} OpenAlex results", Name, results.Count);
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

    private List<SearchResult> ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var resultsArray))
                return results;

            foreach (var item in resultsArray.EnumerateArray())
            {
                try
                {
                    var title = item.GetProperty("title").GetString() ?? "";

                    // Abstract (reconstructed from inverted index)
                    var content = ReconstructAbstract(item);

                    // URLs
                    var url = ExtractPrimaryUrl(item);
                    var htmlUrl = ExtractLandingPageUrl(item);
                    var pdfUrl = ExtractPdfUrl(item);

                    // Authors
                    var authors = ExtractAuthors(item);

                    // Bibliographic info
                    var (journal, publisher, pages, volume, number) = ExtractBiblio(item);

                    // Published date
                    DateTime? publishedDate = null;
                    if (item.TryGetProperty("publication_date", out var pubDateEl))
                    {
                        var dateStr = pubDateEl.GetString();
                        if (!string.IsNullOrEmpty(dateStr))
                            publishedDate = ParseDate(dateStr);
                    }

                    // DOI
                    var doi = "";
                    if (item.TryGetProperty("doi", out var doiEl))
                    {
                        doi = doiEl.GetString() ?? "";
                        doi = doi.Replace("https://doi.org/", "");
                    }

                    // Tags (concepts)
                    var tags = new List<string>();
                    if (item.TryGetProperty("concepts", out var concepts))
                    {
                        foreach (var concept in concepts.EnumerateArray())
                        {
                            if (concept.TryGetProperty("display_name", out var dn))
                            {
                                var name = dn.GetString();
                                if (!string.IsNullOrEmpty(name))
                                    tags.Add(name);
                            }
                        }
                    }

                    // Type
                    var pubType = item.GetProperty("type").GetString() ?? "";

                    // Comments (cited by count)
                    var comments = "";
                    if (item.TryGetProperty("cited_by_count", out var citedBy))
                        comments = $"{citedBy.GetInt32()} citations";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content ?? "",
                        PublishedDate = publishedDate,
                        Authors = authors,
                        Journal = journal,
                        Source = publisher,
                        Doi = doi,
                        Tags = tags,
                        PdfUrl = pdfUrl,
                        Comments = comments,
                        Pages = pages,
                        Volume = volume,
                        Number = number,
                        Metadata = pubType,
                        Engine = Name,
                        Type = SearchResultType.Paper,
                        Category = SearchCategory.Science,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse OpenAlex work item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON response", Name);
        }

        return results;
    }

    /// <summary>
    /// Reconstructs abstract text from OpenAlex's inverted index format.
    /// </summary>
    private static string? ReconstructAbstract(JsonElement item)
    {
        if (!item.TryGetProperty("abstract_inverted_index", out var index) || index.ValueKind != JsonValueKind.Object)
            return null;

        var positionToToken = new Dictionary<int, string>();
        var maxIndex = -1;

        foreach (var token in index.EnumerateObject())
        {
            foreach (var pos in token.Value.EnumerateArray())
            {
                var position = pos.GetInt32();
                positionToToken[position] = token.Name;
                maxIndex = Math.Max(maxIndex, position);
            }
        }

        if (maxIndex < 0) return null;

        var ordered = new List<string>();
        for (int i = 0; i <= maxIndex; i++)
        {
            if (positionToToken.TryGetValue(i, out var token))
                ordered.Add(token);
        }

        var text = string.Join(" ", ordered.Where(t => t != ""));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string ExtractPrimaryUrl(JsonElement item)
    {
        if (item.TryGetProperty("primary_location", out var loc) && loc.TryGetProperty("landing_page_url", out var url))
        {
            var landingUrl = url.GetString();
            if (!string.IsNullOrEmpty(landingUrl))
                return landingUrl;
        }
        return item.GetProperty("id").GetString() ?? "";
    }

    private static string ExtractLandingPageUrl(JsonElement item)
    {
        if (item.TryGetProperty("primary_location", out var loc) && loc.TryGetProperty("landing_page_url", out var url))
            return url.GetString() ?? "";
        return "";
    }

    private static string ExtractPdfUrl(JsonElement item)
    {
        // Check primary_location first
        if (item.TryGetProperty("primary_location", out var loc) && loc.TryGetProperty("pdf_url", out var pdfUrl))
        {
            var pdf = pdfUrl.GetString();
            if (!string.IsNullOrEmpty(pdf)) return pdf;
        }

        // Fallback to open_access.oa_url
        if (item.TryGetProperty("open_access", out var oa) && oa.TryGetProperty("oa_url", out var oaUrl))
            return oaUrl.GetString() ?? "";

        return "";
    }

    private static List<string> ExtractAuthors(JsonElement item)
    {
        var authors = new List<string>();
        if (!item.TryGetProperty("authorships", out var authorships))
            return authors;

        foreach (var auth in authorships.EnumerateArray())
        {
            if (auth.TryGetProperty("author", out var authorObj) && authorObj.TryGetProperty("display_name", out var dn))
            {
                var name = dn.GetString();
                if (!string.IsNullOrEmpty(name))
                    authors.Add(name);
            }
        }

        return authors;
    }

    private static (string journal, string publisher, string pages, string volume, string number) ExtractBiblio(JsonElement item)
    {
        var journal = "";
        var publisher = "";
        var pages = "";
        var volume = "";
        var number = "";

        if (item.TryGetProperty("primary_location", out var loc))
        {
            if (loc.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                if (source.TryGetProperty("display_name", out var dn))
                    journal = dn.GetString() ?? "";
                if (source.TryGetProperty("publisher", out var pub))
                    publisher = pub.GetString() ?? "";
            }
        }

        if (item.TryGetProperty("biblio", out var biblio))
        {
            if (biblio.TryGetProperty("volume", out var vol))
                volume = vol.GetString() ?? "";
            if (biblio.TryGetProperty("issue", out var iss))
                number = iss.GetString() ?? "";

            if (biblio.TryGetProperty("first_page", out var fp) && biblio.TryGetProperty("last_page", out var lp))
            {
                var first = fp.GetString() ?? "";
                var last = lp.GetString() ?? "";
                if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last))
                    pages = $"{first}-{last}";
                else if (!string.IsNullOrEmpty(first))
                    pages = first;
                else if (!string.IsNullOrEmpty(last))
                    pages = last;
            }
        }

        return (journal, publisher, pages, volume, number);
    }

    private static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        // Try YYYY-MM-DD, YYYY-MM, YYYY
        string[] formats = { "yyyy-MM-dd", "yyyy-MM", "yyyy" };
        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(value, fmt, null, System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
        }
        return null;
    }
}
