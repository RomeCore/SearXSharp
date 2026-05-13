namespace SearXSharp;

/// <summary>
/// Represents a search query to be executed by one or more search engines.
/// Based on SearXNG's SearchQuery model with common filtering options.
/// </summary>
public class SearchQuery
{
    /// <summary>
    /// The search terms entered by the user.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// The page number to retrieve (1-based indexing).
    /// </summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// The search category (general, images, videos, news, etc.).
    /// </summary>
    public SearchCategory Category { get; init; } = SearchCategory.General;

    /// <summary>
    /// The safe search filtering level.
    /// </summary>
    public SafeSearchLevel SafeSearch { get; init; } = SafeSearchLevel.None;

    /// <summary>
    /// The language/locale for search results (e.g., "en", "ru", "fr").
    /// Use "auto" for automatic detection.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Optional time range filter for results.
    /// </summary>
    public TimeRange? TimeRange { get; init; }

    /// <summary>
    /// The maximum number of results to return.
    /// </summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>
    /// The request timeout in seconds.
    /// </summary>
    public double TimeoutSeconds { get; init; } = 30.0;
}
