using SearXSharp.Models;
using System.Xml.Linq;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for arXiv (arxiv.org).
/// Uses the arXiv API (export.arxiv.org/api/query) to search for scientific papers.
/// Based on SearXNG's <c>arxiv.py</c> with support for paging, authors, DOI, and PDF links.
/// </summary>
public class ArxivSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://export.arxiv.org/api/query";
    private const int _maxResults = 10;

    /// <summary>
    /// XML namespaces used in arXiv's Atom response.
    /// </summary>
    private static readonly XNamespace _atomNs = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace _arxivNs = "http://arxiv.org/schemas/atom";

    /// <inheritdoc />
    public override string Name => "arxiv";

    /// <inheritdoc />
    public override string DisplayName => "arXiv";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Science, SearchCategory.IT };

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

    /// <summary>
    /// Initializes a new instance of the arXiv search engine.
    /// </summary>
    public ArxivSearchEngine() : base() { }

    /// <summary>
    /// Initializes a new instance with a logger.
    /// </summary>
    public ArxivSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var start = (query.Page - 1) * _maxResults;
            var encodedQuery = Uri.EscapeDataString($"all:{query.Query}");
            var url = $"{_baseUrl}?search_query={encodedQuery}&start={start}&max_results={_maxResults}";

            using var request = CreateGetRequest(url);
            // arXiv API returns XML, so accept it
            request.Headers.TryAddWithoutValidation("Accept", "application/xml, text/xml");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            var results = ParseAtomResponse(xml);

            _logger.Debug("{Engine}: Parsed {Count} papers", Name, results.Count);
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

    /// <summary>
    /// Parses the arXiv Atom XML response.
    /// Based on SearXNG's <c>arxiv.py response()</c> with XPath converted to LINQ to XML.
    /// </summary>
    private List<SearchResult> ParseAtomResponse(string xml)
    {
        var results = new List<SearchResult>();

        try
        {
            var doc = XDocument.Parse(xml);
            var entries = doc.Descendants(_atomNs + "entry");

            var position = 1;
            foreach (var entry in entries)
            {
                try
                {
                    var title = entry.Element(_atomNs + "title")?.Value?.Trim() ?? string.Empty;
                    var url = entry.Element(_atomNs + "id")?.Value?.Trim() ?? string.Empty;
                    var summary = entry.Element(_atomNs + "summary")?.Value?.Trim() ?? string.Empty;

                    // Authors
                    var authors = entry.Elements(_atomNs + "author")
                        .Select(a => a.Element(_atomNs + "name")?.Value?.Trim())
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Cast<string>()
                        .ToList();

                    // DOI
                    var doi = entry.Element(_arxivNs + "doi")?.Value?.Trim();

                    // PDF link
                    var pdfUrl = entry.Elements(_atomNs + "link")
                        .FirstOrDefault(l => l.Attribute("title")?.Value == "pdf")
                        ?.Attribute("href")?.Value;

                    // Journal reference
                    var journal = entry.Element(_arxivNs + "journal_ref")?.Value?.Trim();

                    // Categories/tags
                    var tags = entry.Elements(_atomNs + "category")
                        .Select(c => c.Attribute("term")?.Value)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Cast<string>()
                        .ToList();

                    // Comments
                    var comments = entry.Element(_arxivNs + "comment")?.Value?.Trim();

                    // Published date
                    DateTime? publishedDate = null;
                    var publishedStr = entry.Element(_atomNs + "published")?.Value;
                    if (!string.IsNullOrEmpty(publishedStr))
                    {
                        if (DateTime.TryParse(publishedStr, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            publishedDate = dt;
                    }

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = Truncate(summary, 300),
                        Engine = Name,
                        Type = SearchResultType.Paper,
                        Category = SearchCategory.Science,
                        PublishedDate = publishedDate,
                        Authors = authors,
                        Doi = doi,
                        PdfUrl = pdfUrl,
                        Journal = journal,
                        Tags = tags,
                        Comments = comments,
                        Position = position++,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse an entry", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse Atom XML", Name);
        }

        return results;
    }

    /// <summary>
    /// Truncates a string to the specified maximum length.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
