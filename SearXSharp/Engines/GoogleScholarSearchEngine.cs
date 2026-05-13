using AngleSharp;
using AngleSharp.Dom;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Google Scholar (scholar.google.com).
/// Google Scholar is a freely accessible web search engine that indexes scholarly literature.
/// Based on SearXNG's google_scholar.py.
/// </summary>
public partial class GoogleScholarSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://scholar.google.com";

    /// <inheritdoc />
    public override string Name => "googlescholar";

    /// <inheritdoc />
    public override string DisplayName => "Google Scholar";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Science };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => true;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 50;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    // Time range mapping (all SearXNG ranges map to "year" for Scholar)
    private static readonly HashSet<TimeRange> _supportedTimeRanges = new()
    {
        TimeRange.Day, TimeRange.Week, TimeRange.Month, TimeRange.Year
    };

    public GoogleScholarSearchEngine() : base() { }
    public GoogleScholarSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["start"] = ((query.Page - 1) * 10).ToString(),
                ["as_sdt"] = "2007",   // include patents
                ["as_vis"] = "0",       // include citations
            };

            // Time range - map all to last year
            if (query.TimeRange.HasValue && _supportedTimeRanges.Contains(query.TimeRange.Value))
            {
                args["as_ylo"] = (DateTime.UtcNow.Year - 1).ToString();
            }

            var url = _baseUrl + "/scholar?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Referer", "https://scholar.google.com/");

            var response = await SendRequestAsync(request, ct);

            // Handle redirects (CAPTCHA or access denied)
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                if (location.Contains("/sorry/index"))
                {
                    _logger.Warning("{Engine}: CAPTCHA or unusual traffic detected", Name);
                    return CreateErrorResult("captcha", suspended: true);
                }
                _logger.Warning("{Engine}: Redirected to {Location}", Name, location);
                return CreateErrorResult("redirect");
            }

            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);

            // Check for CAPTCHA in HTML
            if (html.Contains("gs_captcha_f"))
            {
                _logger.Warning("{Engine}: CAPTCHA detected in response", Name);
                return CreateErrorResult("captcha", suspended: true);
            }

            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} scholarly results", Name, results.Count);
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

            // Each result is in a div with data-rp attribute
            var items = document.QuerySelectorAll("div[data-rp]");

            foreach (var item in items)
            {
                try
                {
                    // Title
                    var titleLink = item.QuerySelector("h3 a");
                    if (titleLink == null)
                    {
                        // Skip citation blocks (no title link)
                        var citationText = item.QuerySelector("h3")?.TextContent;
                        if (!string.IsNullOrEmpty(citationText) && citationText.Contains("[ZITATION]"))
                            continue;
                        continue;
                    }

                    var title = titleLink.TextContent.Trim();
                    var url = titleLink.GetAttribute("href") ?? string.Empty;
                    if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url))
                        continue;

                    // Publication type
                    var pubTypeEl = item.QuerySelector("span.gs_ctg2");
                    var pubType = pubTypeEl?.TextContent.Trim().Trim('[', ']') ?? "";
                    var hasPdf = pubType == "PDF";
                    if (hasPdf) pubType = "";

                    // Content / abstract
                    var contentEl = item.QuerySelector("div.gs_rs");
                    var content = contentEl?.TextContent.Trim() ?? "";

                    // Authors, journal, publisher, year (green text div.gs_a)
                    var gsA = item.QuerySelector("div.gs_a");
                    var gsAText = gsA?.TextContent.Trim() ?? "";

                    var (authors, journal, publisher, publishedDate) = ParseGsA(gsAText);

                    // Cited by
                    var comments = "";
                    var citedByLink = item.QuerySelector("div.gs_fl a[href^='/scholar?cites=']");
                    if (citedByLink != null)
                        comments = citedByLink.TextContent.Trim();

                    // PDF / HTML link
                    string htmlUrl = "", pdfUrl = "";
                    var docLink = item.QuerySelector("div.gs_or_ggsm a");
                    if (docLink != null)
                    {
                        var docHref = docLink.GetAttribute("href") ?? "";
                        if (hasPdf)
                            pdfUrl = docHref;
                        else
                            htmlUrl = docHref;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Authors = authors,
                        Journal = journal,
                        Source = publisher,
                        Comments = comments,
                        PdfUrl = pdfUrl,
                        Engine = Name,
                        Type = SearchResultType.Paper,
                        Category = SearchCategory.Science,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse scholar result", Name);
                }
            }

            // Parse suggestions
            // (skipping for now - not critical)
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }

    /// <summary>
    /// Parses the green metadata text from Google Scholar results.
    /// Format: "{authors} - {journal}, {year} - {publisher}" or "{authors} - {year} - {publisher}"
    /// </summary>
    private static (List<string> authors, string journal, string publisher, DateTime? publishedDate) ParseGsA(string text)
    {
        var authors = new List<string>();
        var journal = "";
        var publisher = "";
        DateTime? publishedDate = null;

        if (string.IsNullOrEmpty(text))
            return (authors, journal, publisher, publishedDate);

        var parts = text.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return (authors, journal, publisher, publishedDate);

        // First part is always authors
        authors = parts[0].Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();

        // Last part is publisher
        publisher = parts[^1].Trim();

        // Middle parts contain journal and year
        if (parts.Length == 3)
        {
            var journalYear = parts[1].Split(", ", StringSplitOptions.RemoveEmptyEntries);
            if (journalYear.Length > 1)
            {
                journal = string.Join(", ", journalYear.Take(journalYear.Length - 1));
                if (journal == "…") journal = "";
            }

            var yearStr = journalYear[^1].Trim();
            if (int.TryParse(yearStr, out var year) && year > 1900 && year < 2100)
                publishedDate = new DateTime(year, 1, 1);
        }

        return (authors, journal, publisher, publishedDate);
    }
}
