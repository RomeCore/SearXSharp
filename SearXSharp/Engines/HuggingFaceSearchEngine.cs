using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Hugging Face (models, datasets, spaces).
/// Uses the official Hugging Face Hub API.
/// Based on SearXNG's huggingface.py.
/// </summary>
public class HuggingFaceSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://huggingface.co";

    /// <inheritdoc />
    public override string Name => "huggingface";

    /// <inheritdoc />
    public override string DisplayName => "Hugging Face";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.IT, SearchCategory.Repos, SearchCategory.Science };

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
    /// The Hugging Face endpoint type: "models", "datasets", or "spaces".
    /// </summary>
    public string Endpoint { get; set; } = "models";

    public HuggingFaceSearchEngine() : base() { }
    public HuggingFaceSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["direction"] = "-1",
                ["search"] = query.Query,
            };

            var url = $"{_baseUrl}/api/{Endpoint}?"
                + string.Join("&", queryParams.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = CreateGetRequest(url);
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

            foreach (var entry in root.EnumerateArray())
            {
                try
                {
                    var id = entry.GetProperty("id").GetString() ?? "";

                    var url = Endpoint == "models"
                        ? $"{_baseUrl}/{id}"
                        : $"{_baseUrl}/{Endpoint}/{id}";

                    DateTime? publishedDate = null;
                    if (entry.TryGetProperty("createdAt", out var createdAt))
                    {
                        if (DateTime.TryParse(createdAt.GetString(), out var dt))
                            publishedDate = dt;
                    }

                    var contents = new List<string>();

                    if (entry.TryGetProperty("likes", out var likes))
                        contents.Add($"Likes: {likes.GetRawText()}");

                    if (entry.TryGetProperty("downloads", out var downloads))
                        contents.Add($"Downloads: {downloads.GetInt64():N0}");

                    if (entry.TryGetProperty("tags", out var tags))
                    {
                        var tagList = tags.EnumerateArray()
                            .Select(t => t.GetString() ?? "")
                            .Where(t => !string.IsNullOrEmpty(t));
                        contents.Add($"Tags: {string.Join(", ", tagList)}");
                    }

                    if (entry.TryGetProperty("description", out var desc)
                        && desc.ValueKind == JsonValueKind.String)
                        contents.Add($"Description: {desc.GetString()}");

                    var content = string.Join(" | ", contents);

                    results.Add(new SearchResult
                    {
                        Url = url,
                        Title = id,
                        Content = content,
                        PublishedDate = publishedDate,
                        Engine = Name,
                        Category = SearchCategory.Repos,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse entry", Name);
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
