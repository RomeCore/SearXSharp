using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine for National Vulnerability Database (nvd.nist.gov).
/// Searches for CVE (Common Vulnerabilities and Exposures) records.
/// Based on SearXNG's nvd.py.
/// </summary>
public class NVDSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://nvd.nist.gov/extensions/nudp/services/json/nvd/cve/search/results";
    private const int _resultsPerPage = 10;

    /// <inheritdoc />
    public override string Name => "nvd";

    /// <inheritdoc />
    public override string DisplayName => "NVD";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT };

    /// <inheritdoc />
    public override bool SupportsPaging => true;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 10;
    public override double Timeout => 10.0;

    public NVDSearchEngine() : base() { }
    public NVDSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var startIndex = (query.Page - 1) * _resultsPerPage;

            var args = new Dictionary<string, string>
            {
                ["resultType"] = "records",
                ["keyword"] = query.Query,
                ["rowCount"] = _resultsPerPage.ToString(),
                ["offset"] = startIndex.ToString(),
            };

            var url = _baseUrl + "?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Referer", "https://nvd.nist.gov/vuln/search");

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var results = ParseJson(json);

            _logger.Debug("{Engine}: Parsed {Count} CVE results", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var vulns = doc.RootElement.GetProperty("response")[0].GetProperty("grid").GetProperty("vulnerabilities");

            foreach (var item in vulns.EnumerateArray())
            {
                var cve = item.GetProperty("cve");
                var cveId = cve.GetProperty("id").GetString() ?? "";
                var descriptions = cve.GetProperty("descriptions");
                var description = descriptions.GetArrayLength() > 0
                    ? descriptions[0].GetProperty("value").GetString() ?? ""
                    : "";

                DateTime? publishedDate = null;
                if (cve.TryGetProperty("published", out var pubDate))
                {
                    var dateStr = pubDate.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
                        publishedDate = dt;
                }

                // Severity & CVSS score
                var metadata = "";
                if (cve.TryGetProperty("metrics", out var metrics))
                {
                    var cvssV31 = metrics.GetProperty("cvssMetricV31");
                    if (cvssV31.GetArrayLength() > 0)
                    {
                        var cvssData = cvssV31[0].GetProperty("cvssData");
                        var severity = cvssData.GetProperty("baseSeverity").GetString() ?? "";
                        var score = cvssData.GetProperty("baseScore").GetDouble();
                        metadata = $"Severity: {severity} | CVSS Score: {score}";
                    }
                }

                results.Add(new SearchResult
                {
                    Url = $"https://nvd.nist.gov/vuln/detail/{cveId}",
                    Title = cveId,
                    Content = description,
                    PublishedDate = publishedDate,
                    Metadata = metadata,
                    Engine = Name,
                    Category = SearchCategory.IT,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return results;
    }
}
