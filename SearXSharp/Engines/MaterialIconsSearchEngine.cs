using SearXSharp.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Material Icons (fonts.google.com/icons).
/// Google Material Symbols and Icons search.
/// Based on SearXNG's material_icons.py.
/// </summary>
public partial class MaterialIconsSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://fonts.google.com/metadata/icons?key=material_symbols&incomplete=true";
    private const string _resultUrl = "https://fonts.google.com/icons?icon.query={query}&selected=Material+Symbols+Outlined:{icon_name}:FILL@0{fill};wght@400;GRAD@0;opsz@24";
    private const string _imgSrcUrl = "https://fonts.gstatic.com/s/i/short-term/release/materialsymbolsoutlined/{icon_name}/{svg_type}/24px.svg";

    /// <inheritdoc />
    public override string Name => "material_icons";

    /// <inheritdoc />
    public override string DisplayName => "Material Icons";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => false;
    public override bool SupportsTimeRange => false;
    public override bool SupportsSafeSearch => false;
    public override int MaxPages => 1;
    public override double Timeout => 10.0;

    public MaterialIconsSearchEngine() : base() { }
    public MaterialIconsSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        try
        {
            var request = CreateGetRequest(_searchUrl);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            // Remove the first 5 characters (")]}' \n" prefix)
            if (json.Length > 5)
                json = json[5..];

            var results = ParseJson(json, query.Query);

            _logger.Debug("{Engine}: Parsed {Count} icon results", Name, results.Count);
            return CreateResultList(results);
        }
        catch (TaskCanceledException) { return CreateErrorResult("timeout", suspended: true); }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private List<SearchResult> ParseJson(string json, string query)
    {
        var results = new List<SearchResult>();
        var queryLower = query.ToLower();

        // Check for "filled" in query
        var filledRegex = FilledRegex();
        var outlined = !filledRegex.IsMatch(queryLower);
        var queryClean = filledRegex.Replace(queryLower, "").Trim();
        var svgType = outlined ? "default" : "fill1";

        var queryParts = queryClean.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var icons = doc.RootElement.GetProperty("icons");

            foreach (var icon in icons.EnumerateArray())
            {
                var iconName = icon.GetProperty("name").GetString() ?? "";
                var tags = icon.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
                var categories = icon.GetProperty("categories").EnumerateArray().Select(c => c.GetString() ?? "").ToList();

                // Check if any query part matches name, tags, or categories
                var match = false;
                foreach (var part in queryParts)
                {
                    if (iconName.Contains(part) ||
                        tags.Any(t => t.Contains(part)) ||
                        categories.Any(c => c.Contains(part)))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match && queryParts.Length > 0) continue;

                var title = iconName.Replace("_", "").Replace("_", "");
                // Title case
                title = string.Join(" ", title.Split('_').Select(w =>
                    w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));

                var content = string.Join(", ", tags.Select(t => ToTitleCase(t)))
                    + " / " + string.Join(", ", categories.Select(c => ToTitleCase(c)));

                results.Add(new SearchResult
                {
                    Url = _resultUrl
                        .Replace("{icon_name}", iconName)
                        .Replace("{query}", iconName)
                        .Replace("{fill}", outlined ? "0" : "1"),
                    Title = title,
                    Content = content,
                    ImgSrc = _imgSrcUrl
                        .Replace("{icon_name}", iconName)
                        .Replace("{svg_type}", svgType),
                    Thumbnail = _imgSrcUrl
                        .Replace("{icon_name}", iconName)
                        .Replace("{svg_type}", svgType),
                    Engine = Name,
                    Type = SearchResultType.Image,
                    Category = SearchCategory.Images,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return results;
    }

    private static string ToTitleCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        return char.ToUpper(str[0]) + str[1..];
    }

    [GeneratedRegex(@"(fill)(ed)?", RegexOptions.IgnoreCase)]
    private static partial Regex FilledRegex();
}
