namespace SearXSharp;

/// <summary>
/// Defines the interface for all search engines.
/// Each engine implementation wraps a specific search provider (Bing, Google, Brave, etc.)
/// and handles its specific request/response format, anti-bot measures, and parsing logic.
/// Based on the SearXNG engine architecture (request/response pattern).
/// </summary>
public interface ISearchEngine
{
    /// <summary>
    /// Gets the unique name identifier for this engine (e.g., "bing", "google", "brave").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the human-readable display name for this engine.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the list of categories this engine supports.
    /// </summary>
    IReadOnlyList<SearchCategory> SupportedCategories { get; }

    /// <summary>
    /// Gets whether this engine supports paging (multiple result pages).
    /// </summary>
    bool SupportsPaging { get; }

    /// <summary>
    /// Gets whether this engine supports time range filtering.
    /// </summary>
    bool SupportsTimeRange { get; }

    /// <summary>
    /// Gets whether this engine supports safe search filtering.
    /// </summary>
    bool SupportsSafeSearch { get; }

    /// <summary>
    /// Gets the maximum number of pages this engine can return (0 = unlimited).
    /// </summary>
    int MaxPages { get; }

    /// <summary>
    /// Gets the default request timeout in seconds for this engine.
    /// </summary>
    double Timeout { get; }

    /// <summary>
    /// Performs a search query against this engine and returns structured results.
    /// </summary>
    /// <param name="query">The search query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that resolves to a search result list.</returns>
    Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default);
}
