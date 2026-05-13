using SearXSharp.Models;

namespace SearXSharp;

/// <summary>
/// Aggregates all results from one or more search engines into a single response.
/// Based on SearXNG's ResultContainer with merged results, suggestions, corrections, and infoboxes.
/// </summary>
public class SearchResultList
{
    /// <summary>
    /// The main search results (deduplicated and sorted by relevance).
    /// </summary>
    public required IReadOnlyList<SearchResult> Results { get; init; }

    /// <summary>
    /// Search suggestions (e.g., related queries).
    /// </summary>
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Spelling corrections for the query.
    /// </summary>
    public IReadOnlyList<string> Corrections { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Infobox results containing structured entity data.
    /// </summary>
    public IReadOnlyList<SearchResult> Infoboxes { get; init; } = Array.Empty<SearchResult>();

    /// <summary>
    /// Direct answers to the query (currency, calculation, weather, etc.).
    /// </summary>
    public IReadOnlyList<SearchAnswer> Answers { get; init; } = Array.Empty<SearchAnswer>();

    /// <summary>
    /// The estimated total number of results (from engine metadata).
    /// </summary>
    public long? TotalResults { get; init; }

    /// <summary>
    /// Timing information for each engine that was queried.
    /// </summary>
    public IReadOnlyList<EngineTiming> EngineTimings { get; init; } = Array.Empty<EngineTiming>();

    /// <summary>
    /// Engines that failed to respond or encountered errors.
    /// </summary>
    public IReadOnlyList<UnresponsiveEngineInfo> UnresponsiveEngines { get; init; } = Array.Empty<UnresponsiveEngineInfo>();

    /// <summary>
    /// Whether the result set contains paging support.
    /// </summary>
    public bool SupportsPaging { get; init; }
}
