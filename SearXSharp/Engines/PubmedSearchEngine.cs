using SearXSharp.Models;
using System.Xml.Linq;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for PubMed (biomedical literature).
/// Uses NCBI's E-utilities API: esearch.fcgi to find IDs, then efetch.fcgi for details.
/// Based on SearXNG's pubmed.py.
/// </summary>
public class PubmedSearchEngine : SearchEngineBase
{
    private const string _eutilsApi = "https://eutils.ncbi.nlm.nih.gov/entrez/eutils";
    private const string _pubmedUrl = "https://www.ncbi.nlm.nih.gov/pubmed/";
    private const int _numberOfResults = 10;

    // XML namespaces for PubMed DTD
    private static readonly XNamespace _ns = "http://www.w3.org/2005/Atom";

    /// <inheritdoc />
    public override string Name => "pubmed";

    /// <inheritdoc />
    public override string DisplayName => "PubMed";

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
    public override double Timeout => 15.0;

    public PubmedSearchEngine() : base() { }
    public PubmedSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            // Step 1: ESearch - get PMIDs
            var retStart = (query.Page - 1) * _numberOfResults;
            var esearchUrl = $"{_eutilsApi}/esearch.fcgi?db=pubmed&term={Uri.EscapeDataString(query.Query)}&retstart={retStart}&retmax={_numberOfResults}";

            using var esearchRequest = CreateGetRequest(esearchUrl);
            var esearchResponse = await SendRequestAsync(esearchRequest, ct);
            esearchResponse.EnsureSuccessStatusCode();

            var esearchXml = await esearchResponse.Content.ReadAsStringAsync(ct);
            var pmids = ExtractPmids(esearchXml);

            if (pmids.Count == 0)
                return CreateResultList(new List<SearchResult>());

            // Step 2: EFetch - get details for those PMIDs
            var ids = string.Join(",", pmids);
            var efetchUrl = $"{_eutilsApi}/efetch.fcgi?db=pubmed&retmode=xml&id={ids}";

            using var efetchRequest = CreateGetRequest(efetchUrl);
            var efetchResponse = await SendRequestAsync(efetchRequest, ct);
            efetchResponse.EnsureSuccessStatusCode();

            var efetchXml = await efetchResponse.Content.ReadAsStringAsync(ct);
            return ParseEfetchResponse(efetchXml);
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
    /// Extracts PMIDs from ESearch XML response.
    /// XPath: //eSearchResult/IdList/Id
    /// </summary>
    private static List<string> ExtractPmids(string xml)
    {
        var pmids = new List<string>();
        try
        {
            var doc = XDocument.Parse(xml);
            pmids = doc.Descendants("IdList").Elements("Id")
                .Select(i => i.Value.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse ESearch XML: {ex.Message}");
        }
        return pmids;
    }

    /// <summary>
    /// Parses EFetch XML response (PubMed DTD: https://dtd.nlm.nih.gov/ncbi/pubmed/out/pubmed_250101.dtd).
    /// Based on SearXNG's pubmed.py response() function.
    /// </summary>
    private SearchResultList ParseEfetchResponse(string xml)
    {
        var results = new List<SearchResult>();

        try
        {
            var doc = XDocument.Parse(xml);

            foreach (var pubmedArticle in doc.Descendants("PubmedArticle"))
                {
                try
                {
                    var medlineCitation = pubmedArticle.Element("MedlineCitation");
                    var pubmedData = pubmedArticle.Element("PubmedData");

                    if (medlineCitation == null) continue;

                    // Title: MedlineCitation/Article/ArticleTitle
                    var title = GetFieldText(medlineCitation, ".//Article/ArticleTitle") ?? "";

                    // PMID
                    var pmid = GetFieldText(medlineCitation, ".//PMID") ?? "";
                    var url = _pubmedUrl + pmid;

                    // Abstract: MedlineCitation/Article/Abstract/AbstractText//text()
                    var content = GetFieldText(medlineCitation, ".//Abstract/AbstractText//text()") ?? "";

                    // DOI: MedlineCitation/Article/ELocationID[@EIdType='doi']
                    var doi = "";
                    var doiEl = medlineCitation.Descendants("ELocationID")
                        .FirstOrDefault(e => e.Attribute("EIdType")?.Value == "doi");
                    if (doiEl != null)
                        doi = doiEl.Value.Trim();

                    // Journal: MedlineCitation/Article/Journal/Title
                    var journal = GetFieldText(medlineCitation, "./Article/Journal/Title");

                    // ISSN: MedlineCitation/Article/Journal/ISSN
                    var issn = GetFieldText(medlineCitation, "./Article/Journal/ISSN");

                    // Authors: MedlineCitation/Article/AuthorList/Author
                    var authors = new List<string>();
                    foreach (var author in medlineCitation.Descendants("AuthorList").Elements("Author"))
                    {
                        var f = author.Element("ForeName")?.Value ?? "";
                        var l = author.Element("LastName")?.Value ?? "";
                        var authorName = $"{f} {l}".Trim();
                        if (!string.IsNullOrEmpty(authorName))
                            authors.Add(authorName);
                    }

                    // PublishedDate: from PubmedData/History/PubDate[@PubStatus='accepted']
                    DateTime? publishedDate = null;
                    if (pubmedData != null)
                    {
                        var acceptedDate = pubmedData.Descendants("PubMedPubDate")
                            .FirstOrDefault(p => p.Attribute("PubStatus")?.Value == "accepted");
                        if (acceptedDate != null)
                        {
                            var year = acceptedDate.Element("Year")?.Value;
                            var month = acceptedDate.Element("Month")?.Value;
                            var day = acceptedDate.Element("Day")?.Value;
                            if (int.TryParse(year, out var y))
                            {
                                var m = int.TryParse(month, out var mVal) ? mVal : 1;
                                var d = int.TryParse(day, out var dVal) ? dVal : 1;
                                try { publishedDate = new DateTime(y, m, d); } catch { }
                            }
                        }
                    }

                    // Fallback: try Article/Journal/JournalIssue/PubDate
                    if (publishedDate == null)
                    {
                        var pubDateEl = medlineCitation.Descendants("PubDate").FirstOrDefault();
                        if (pubDateEl != null)
                        {
                            var year = pubDateEl.Element("Year")?.Value;
                            var month = pubDateEl.Element("Month")?.Value;
                            var day = pubDateEl.Element("Day")?.Value;
                            if (int.TryParse(year, out var y))
                            {
                                var m = int.TryParse(month, out var mVal) ? mVal : 1;
                                var d = int.TryParse(day, out var dVal) ? dVal : 1;
                                try { publishedDate = new DateTime(y, m, d); } catch { }
                            }
                        }
                    }

                    // Truncate content
                    if (!string.IsNullOrEmpty(content) && content.Length > 500)
                        content = content[..500] + "...";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = title,
                        Content = content,
                        PublishedDate = publishedDate,
                        Authors = authors,
                        Journal = journal,
                        Doi = doi,
                        Engine = Name,
                        Category = SearchCategory.Science,
                        Type = SearchResultType.Paper,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse PubmedArticle", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse EFetch XML", Name);
        }

        return CreateResultList(results);
    }

    /// <summary>
    /// Safely extracts text from an XML element using an XPath-like descendant search.
    /// </summary>
    private static string? GetFieldText(XElement root, string xpath)
    {
        try
        {
            // Simple descendant-based path resolution
            var parts = xpath.TrimStart('.').TrimStart('/').Split('/');
            IEnumerable<XElement> current = new[] { root };

            foreach (var part in parts)
            {
                if (part == "//text()")
                {
                    // Get all text from descendants
                    var texts = current.SelectMany(e => e.Descendants().Where(d => !d.HasElements))
                        .Select(e => e.Value.Trim())
                        .Where(t => !string.IsNullOrEmpty(t));
                    return string.Join(" ", texts);
                }

                var isDescendant = part.StartsWith("//");
                var cleanPart = part.TrimStart('/');

                if (cleanPart.Contains('[') && cleanPart.Contains(']'))
                {
                    // Handle predicates like [@EIdType='doi']
                    var bracketStart = cleanPart.IndexOf('[');
                    var bracketEnd = cleanPart.IndexOf(']');
                    var predicate = cleanPart[(bracketStart + 1)..bracketEnd];
                    var elementName = cleanPart[..bracketStart];

                    if (predicate.StartsWith("@"))
                    {
                        var attrParts = predicate[1..].Split('=');
                        var attrName = attrParts[0].Trim();
                        var attrValue = attrParts.Length > 1 ? attrParts[1].Trim('\'') : "";

                        current = isDescendant
                            ? current.SelectMany(e => e.Descendants(elementName)
                                .Where(d => d.Attribute(attrName)?.Value == attrValue))
                            : current.SelectMany(e => e.Elements(elementName)
                                .Where(d => d.Attribute(attrName)?.Value == attrValue));
                    }
                }
                else
                {
                    current = isDescendant
                        ? current.SelectMany(e => e.Descendants(cleanPart))
                        : current.SelectMany(e => e.Elements(cleanPart));
                }

                if (!current.Any()) return null;
            }

            return current.FirstOrDefault()?.Value?.Trim();
        }
        catch
        {
            return null;
        }
    }
}
