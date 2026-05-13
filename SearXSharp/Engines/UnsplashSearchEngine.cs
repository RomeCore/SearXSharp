using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Unsplash (royalty-free images).
/// Uses Unsplash's internal napi (not the official API).
/// Based on SearXNG's unsplash.py.
/// </summary>
public class UnsplashSearchEngine : SearchEngineBase
{
    private const string _searchUrl = "https://unsplash.com/napi/search/photos?";

    /// <inheritdoc />
    public override string Name => "unsplash";

    /// <inheritdoc />
    public override string DisplayName => "Unsplash";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Images };

    /// <inheritdoc />
    public override bool SupportsPaging => true;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 20;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public UnsplashSearchEngine() : base() { }
    public UnsplashSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _searchUrl + $"query={Uri.EscapeDataString(query.Query)}&page={query.Page}&per_page=20";

            using var request = CreateGetRequest(url);
            // Unsplash blocks common user agents via Anubis, use a simpler one
            request.Headers.UserAgent.TryParseAdd("SearXSharp/1.0");

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
            _logger.Error(ex, "{Engine}: Search failed", Name);
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

            if (!root.TryGetProperty("results", out var items))
                return CreateResultList(results);

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var htmlLink = CleanUrl(item.GetProperty("links").GetProperty("html").GetString() ?? "");
                    var thumb = CleanUrl(item.GetProperty("urls").GetProperty("thumb").GetString() ?? "");
                    var regular = CleanUrl(item.GetProperty("urls").GetProperty("regular").GetString() ?? "");

                    var altDescription = "";
                    if (item.TryGetProperty("alt_description", out var alt) && alt.ValueKind == JsonValueKind.String)
                        altDescription = alt.GetString() ?? "unknown";

                    var description = "";
                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        description = desc.GetString() ?? "";

                    results.Add(new SearchResult
                    {
                        Url = htmlLink,
                        Title = altDescription,
                        Content = description,
                        Thumbnail = thumb,
                        ImgSrc = regular,
                        Engine = Name,
                        Category = SearchCategory.Images,
                        Type = SearchResultType.Image,
                    });
                }
                catch { /* skip */ }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }

    /// <summary>
    /// Removes the ixid tracking parameter from Unsplash URLs.
    /// </summary>
    private static string CleanUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query.Remove("ixid");
            var cleanedQuery = string.Join("&", query.AllKeys
                .Where(k => k != null)
                .Select(k => $"{Uri.EscapeDataString(k!)}={Uri.EscapeDataString(query[k!] ?? "")}"));

            return cleanedQuery.Length > 0
                ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}?{cleanedQuery}"
                : $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        catch
        {
            return url;
        }
    }
}
