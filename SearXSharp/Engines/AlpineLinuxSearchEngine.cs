using AngleSharp;
using SearXSharp.Models;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Alpine Linux binary packages (pkgs.alpinelinux.org).
/// Alpine Linux is a Linux-based operating system designed to be small, simple and secure.
/// Based on SearXNG's alpinelinux.py.
/// </summary>
public partial class AlpineLinuxSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://pkgs.alpinelinux.org";
    private const string _defaultArch = "x86_64";

    private static readonly Regex _archRegex = ArchRegex();

    /// <inheritdoc />
    public override string Name => "alpinelinux";

    /// <inheritdoc />
    public override string DisplayName => "Alpine Linux";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Packages };

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

    public AlpineLinuxSearchEngine() : base() { }
    public AlpineLinuxSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            // Try to extract architecture from query
            var searchQuery = query.Query;
            var arch = _defaultArch;

            var archMatch = _archRegex.Match(searchQuery);
            if (archMatch.Success)
            {
                arch = archMatch.Groups[0].Value;
                searchQuery = searchQuery.Replace(archMatch.Groups[0].Value, "").Trim();
            }

            var args = new Dictionary<string, string>
            {
                ["name"] = $"*{searchQuery}*",
                ["page"] = query.Page.ToString(),
                ["arch"] = arch,
            };

            var url = _baseUrl + "/packages?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ParseHtml(html);

            _logger.Debug("{Engine}: Parsed {Count} packages for arch {Arch}", Name, results.Count, arch);
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

            // Results are in <table><tbody><tr> elements
            var rows = document.QuerySelectorAll("table tbody tr");

            foreach (var row in rows)
            {
                try
                {
                    var cells = row.QuerySelectorAll("td");
                    if (cells.Length < 9) continue; // Skip "No item found" or invalid rows

                    // Package name & URL
                    var packageCell = row.QuerySelector("td.package");
                    if (packageCell == null) continue;

                    var link = packageCell.QuerySelector("a");
                    var packageName = packageCell.TextContent.Trim();
                    var href = link?.GetAttribute("href") ?? string.Empty;
                    var url = string.IsNullOrEmpty(href) ? "" : _baseUrl + href;

                    // Version
                    var versionCell = row.QuerySelector("td.version");
                    var version = versionCell?.TextContent.Trim() ?? string.Empty;

                    // Build date
                    var bdateCell = row.QuerySelector("td.bdate");
                    var bdateStr = bdateCell?.TextContent.Trim() ?? string.Empty;

                    DateTime? publishedDate = null;
                    if (DateTime.TryParse(bdateStr, out var dt))
                        publishedDate = dt;

                    // Homepage
                    var urlCell = row.QuerySelector("td.url a");
                    var homepage = urlCell?.GetAttribute("href") ?? string.Empty;

                    // Maintainer
                    var maintainerCell = row.QuerySelector("td.maintainer");
                    var maintainer = maintainerCell?.TextContent.Trim() ?? string.Empty;

                    // License
                    var licenseCell = row.QuerySelector("td.license");
                    var license = licenseCell?.TextContent.Trim() ?? string.Empty;

                    // Repo (tag)
                    var repoCell = row.QuerySelector("td.repo");
                    var repo = repoCell?.TextContent.Trim() ?? string.Empty;

                    var content = $"v{version} | {maintainer}";
                    if (!string.IsNullOrEmpty(license))
                        content += $" | License: {license}";

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = packageName,
                        Content = content,
                        PublishedDate = publishedDate,
                        Author = maintainer,
                        Source = homepage,
                        Metadata = version,
                        Tags = new[] { repo }.Where(t => !string.IsNullOrEmpty(t)).ToList(),
                        Engine = Name,
                        Type = SearchResultType.Default,
                        Category = SearchCategory.Packages,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse package row", Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse HTML", Name);
        }

        return results;
    }

    [GeneratedRegex(@"x86_64|x86|aarch64|armhf|ppc64le|s390x|armv7|riscv64", RegexOptions.IgnoreCase)]
    private static partial Regex ArchRegex();
}
