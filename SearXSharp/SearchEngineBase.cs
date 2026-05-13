using SearXSharp.Models;
using System.Diagnostics;

namespace SearXSharp;

/// <summary>
/// Abstract base class for all search engine implementations.
/// Provides common functionality for HTTP communication, user-agent generation,
/// timeout management, and result processing.
/// Based on SearXNG's EngineProcessor and OnlineProcessor architecture.
/// </summary>
public abstract class SearchEngineBase : ISearchEngine, IDisposable
{
    /// <summary>
    /// The HTTP client used for all requests. Shared across requests for connection pooling.
    /// </summary>
    protected readonly HttpClient _httpClient;

    /// <summary>
    /// The logger instance for this engine.
    /// </summary>
    protected readonly ILogger _logger;

    /// <summary>
    /// Random number generator for user-agent selection.
    /// </summary>
    protected static readonly Random _random = new();

    /// <summary>
    /// Common browser user-agent strings for realistic request headers.
    /// Based on SearXNG's USER_AGENTS data from useragents.json.
    /// </summary>
    protected static readonly string[] _userAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.208 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0",
    ];

    /// <summary>
    /// Google-specific user-agent strings (GSA - Google Search App).
    /// Based on SearXNG's gen_gsa_useragent().
    /// </summary>
    protected static readonly string[] _gsaUserAgents =
    [
        "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.6422.165 Mobile Safari/537.36",
        "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.159 Mobile Safari/537.36",
        "Dalvik/2.1.0 (Linux; U; Android 14; Pixel 8 Pro Build/UD1A.230803.041)",
        "Dalvik/2.1.0 (Linux; U; Android 13; SM-S908B Build/TP1A.220624.014)",
    ];

    /// <summary>
    /// Initializes a new instance of the search engine base.
    /// </summary>
    protected SearchEngineBase()
    {
        _httpClient = CreateHttpClient();
        _logger = EmptyLogger.Instance;
    }

    /// <summary>
    /// Initializes a new instance with a specified logger.
    /// </summary>
    protected SearchEngineBase(ILogger logger)
    {
        _httpClient = CreateHttpClient();
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the logger for this engine.
    /// </summary>
    public ILogger Logger
    {
        get => _logger;
        init => _logger = value ?? EmptyLogger.Instance;
    }

    // ——— Abstract members ———

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<SearchCategory> SupportedCategories { get; }

    /// <inheritdoc />
    public abstract bool SupportsPaging { get; }

    /// <inheritdoc />
    public abstract bool SupportsTimeRange { get; }

    /// <inheritdoc />
    public abstract bool SupportsSafeSearch { get; }

    /// <inheritdoc />
    public abstract int MaxPages { get; }

    /// <inheritdoc />
    public abstract double Timeout { get; }

    /// <inheritdoc />
    public abstract Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default);

    // ———— Protected methods ————

    /// <summary>
    /// Creates an HttpClient with appropriate default headers.
    /// Based on SearXNG's network initialization in OnlineProcessor.
    /// </summary>
    protected virtual HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                     | System.Net.DecompressionMethods.Deflate
                                     | System.Net.DecompressionMethods.Brotli,
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en,en-US;q=0.7,en;q=0.3");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        client.DefaultRequestHeaders.Add("DNT", "1");
        client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        client.Timeout = TimeSpan.FromSeconds(Timeout > 0 ? Timeout + 5 : 35);

        return client;
    }

    /// <summary>
    /// Generates a random browser user-agent string.
    /// Equivalent to SearXNG's <c>gen_useragent()</c>.
    /// </summary>
    protected virtual string GetRandomUserAgent()
    {
        return _userAgents[_random.Next(_userAgents.Length)];
    }

    /// <summary>
    /// Generates a random Google Search App user-agent string.
    /// Equivalent to SearXNG's <c>gen_gsa_useragent()</c>.
    /// </summary>
    protected virtual string GetGsaUserAgent()
    {
        return _gsaUserAgents[_random.Next(_gsaUserAgents.Length)];
    }

    /// <summary>
    /// Creates a GET request with standard headers mimicking a browser.
    /// </summary>
    protected virtual HttpRequestMessage CreateGetRequest(string url, bool useGsaAgent = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.TryParseAdd(useGsaAgent ? GetGsaUserAgent() : GetRandomUserAgent());
        return request;
    }

    /// <summary>
    /// Creates a POST request with standard headers and form URL-encoded content.
    /// </summary>
    protected virtual HttpRequestMessage CreatePostRequest(string url, Dictionary<string, string> formData, bool useGsaAgent = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.UserAgent.TryParseAdd(useGsaAgent ? GetGsaUserAgent() : GetRandomUserAgent());
        request.Content = new FormUrlEncodedContent(formData);
        return request;
    }

    /// <summary>
    /// Creates a POST request with JSON content.
    /// </summary>
    protected virtual HttpRequestMessage CreateJsonPostRequest(string url, string json, bool useGsaAgent = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.UserAgent.TryParseAdd(useGsaAgent ? GetGsaUserAgent() : GetRandomUserAgent());
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// Sends an HTTP request and returns the response.
    /// Handles timeout and basic error checking.
    /// </summary>
    protected virtual async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            sw.Stop();

            _logger.Debug("{Engine}: {Method} {Url} -> {Status} in {ElapsedMs}ms",
                Name, request.Method, request.RequestUri, (int)response.StatusCode, sw.ElapsedMilliseconds);

            return response;
        }
        catch (TaskCanceledException)
        {
            _logger.Warning("{Engine}: Request timed out after {Timeout}s: {Url}",
                Name, Timeout, request.RequestUri);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(ex, "{Engine}: HTTP request failed: {Url}", Name, request.RequestUri);
            throw;
        }
    }

    /// <summary>
    /// Validates the search query against the engine's capabilities.
    /// Returns null if the query is valid, or an error message otherwise.
    /// Based on SearXNG's <c>EngineProcessor.get_params()</c> validation logic.
    /// </summary>
    protected virtual string? ValidateQuery(SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return "Query cannot be empty.";
        }

        if (query.Page > 1 && !SupportsPaging)
        {
            return $"Engine '{Name}' does not support paging.";
        }

        if (MaxPages > 0 && query.Page > MaxPages)
        {
            return $"Engine '{Name}' supports a maximum of {MaxPages} pages.";
        }

        if (query.TimeRange.HasValue && !SupportsTimeRange)
        {
            return $"Engine '{Name}' does not support time range filtering.";
        }

        if (query.SafeSearch != SafeSearchLevel.None && !SupportsSafeSearch)
        {
            return $"Engine '{Name}' does not support safe search.";
        }

        return null;
    }

    /// <summary>
    /// Creates a successful SearchResultList with the given results.
    /// </summary>
    protected static SearchResultList CreateResultList(IReadOnlyList<SearchResult> results)
    {
        return new SearchResultList
        {
            Results = results,
        };
    }

    /// <summary>
    /// Creates an empty result list with an error indicator, representing an unresponsive engine.
    /// Based on SearXNG's <c>add_unresponsive_engine</c>.
    /// </summary>
    protected static SearchResultList CreateErrorResult(string errorType, bool suspended = false)
    {
        return new SearchResultList
        {
            Results = Array.Empty<SearchResult>(),
            UnresponsiveEngines = new List<UnresponsiveEngineInfo>
            {
                new(string.Empty, errorType, suspended),
            },
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
