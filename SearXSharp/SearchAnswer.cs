namespace SearXSharp;

/// <summary>
/// Represents a direct answer to the search query (e.g., currency conversion, calculation, weather).
/// Based on SearXNG's Answer result type.
/// </summary>
public class SearchAnswer
{
    /// <summary>
    /// The answer text to display.
    /// </summary>
    public required string Answer { get; init; }

    /// <summary>
    /// Optional URL linking to a resource related to the answer.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// The engine that provided this answer.
    /// </summary>
    public string? Engine { get; init; }
}
