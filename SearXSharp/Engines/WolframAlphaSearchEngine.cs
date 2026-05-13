using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Wolfram|Alpha (wolframalpha.com).
/// Uses the Wolfram|Alpha JSON API (no official API key required).
/// Based on SearXNG's wolframalpha_noapi.py.
/// </summary>
public class WolframAlphaSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://www.wolframalpha.com";
    private const string _searchUrl = _baseUrl + "/input/json.jsp"
        + "?async=false"
        + "&banners=raw"
        + "&debuggingdata=false"
        + "&format=image,plaintext,imagemap,minput,moutput"
        + "&formattimeout=2"
        + "&{query}"
        + "&output=JSON"
        + "&parsetimeout=2"
        + "&scantimeout=0.5"
        + "&sponsorcategories=true"
        + "&statemethod=deploybutton";

    private string? _cachedToken;
    private DateTime? _tokenExpiry;

    /// <inheritdoc />
    public override string Name => "wolframalpha";

    /// <inheritdoc />
    public override string DisplayName => "Wolfram|Alpha";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.Science };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 15.0;

    public WolframAlphaSearchEngine() : base() { }
    public WolframAlphaSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var token = await ObtainTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
                return CreateErrorResult("no_token");

            var queryStr = $"input={Uri.EscapeDataString(query.Query)}";
            var url = _searchUrl.Replace("{query}", queryStr) + $"&proxycode={token}";
            var refererUrl = $"{_baseUrl}/input/?i={Uri.EscapeDataString(query.Query)}";

            using var request = CreateGetRequest(url);
            request.Headers.TryAddWithoutValidation("Referer", refererUrl);

            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(json, refererUrl);
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

    private async Task<string?> ObtainTokenAsync(CancellationToken ct)
    {
        // Check cache
        if (_cachedToken != null && _tokenExpiry.HasValue && DateTime.UtcNow < _tokenExpiry.Value)
            return _cachedToken;

        try
        {
            var url = "https://www.wolframalpha.com/input/api/v1/code?ts=9999999999999999999";
            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            _cachedToken = doc.RootElement.GetProperty("code").GetString() ?? "";
            _tokenExpiry = DateTime.UtcNow.AddHours(1);

            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Failed to obtain token", Name);
            return null;
        }
    }

    private SearchResultList ParseJson(string json, string refererUrl)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("queryresult", out var queryResult)
                || !queryResult.TryGetProperty("success", out var success)
                || !success.GetBoolean())
                return CreateResultList(results);

            if (!queryResult.TryGetProperty("pods", out var pods))
                return CreateResultList(results);

            var infoboxTitle = "";
            var resultContent = "";
            var resultChunks = new List<(string Label, string? Value, string? Image)>();

            foreach (var pod in pods.EnumerateArray())
            {
                var podId = pod.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var podTitle = pod.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                var podIsResult = pod.TryGetProperty("primary", out _);

                if (!pod.TryGetProperty("subpods", out var subpods))
                    continue;

                if (podId == "Input" || string.IsNullOrEmpty(infoboxTitle))
                {
                    if (subpods.GetArrayLength() > 0 && subpods[0].TryGetProperty("plaintext", out var pt))
                        infoboxTitle = pt.GetString() ?? "";
                }

                foreach (var subpod in subpods.EnumerateArray())
                {
                    var plaintext = subpod.TryGetProperty("plaintext", out var pt2)
                        ? pt2.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(plaintext) && podId != "VisualRepresentation"
                        && podId != "Illustration" && podId != "Symbol")
                    {
                        if (plaintext != "(requires interactivity)")
                            resultChunks.Add((podTitle, plaintext, null));

                        if (podIsResult || string.IsNullOrEmpty(resultContent))
                        {
                            if (podId != "Input")
                                resultContent = $"{podTitle}: {plaintext}";
                        }
                    }
                    else if (subpod.TryGetProperty("img", out var img))
                    {
                        var imgSrc = img.GetProperty("src").GetString() ?? "";
                        resultChunks.Add((podTitle, null, imgSrc));
                    }
                }
            }

            if (resultChunks.Count == 0)
                return CreateResultList(results);

            // Add as a normal result
            results.Add(new SearchResult
            {
                Url = refererUrl,
                Title = $"Wolfram|Alpha ({infoboxTitle})",
                Content = resultContent,
                Engine = Name,
                Category = SearchCategory.Science,
            });
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
