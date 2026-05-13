namespace SearXSharp;

/// <summary>
/// Defines the type of a search result, which determines how it is rendered and processed.
/// Based on SearXNG's result type system.
/// </summary>
public enum SearchResultType
{
    /// <summary>
    /// A standard web page result (title, URL, content snippet).
    /// </summary>
    Default,

    /// <summary>
    /// An image result with thumbnail and source URL.
    /// </summary>
    Image,

    /// <summary>
    /// A video result with duration, thumbnail, and optional embed URL.
    /// </summary>
    Video,

    /// <summary>
    /// A news article result with source and publication date.
    /// </summary>
    News,

    /// <summary>
    /// A downloadable file result (PDF, archive, etc.).
    /// </summary>
    File,

    /// <summary>
    /// A code snippet result (GitHub Gist, StackOverflow, etc.).
    /// </summary>
    Code,

    /// <summary>
    /// An academic paper result (arXiv, CrossRef, etc.) with authors and DOI.
    /// </summary>
    Paper,

    /// <summary>
    /// A direct answer to the query (currency conversion, calculation, weather, etc.).
    /// </summary>
    Answer,

    /// <summary>
    /// A key-value structured result (command output, system info, etc.).
    /// </summary>
    KeyValue,

    /// <summary>
    /// A translation result with source and target text.
    /// </summary>
    Translation,

    /// <summary>
    /// A search suggestion (e.g. "Did you mean ...").
    /// </summary>
    Suggestion,

    /// <summary>
    /// A spelling correction for the search query.
    /// </summary>
    Correction,

    /// <summary>
    /// An infobox with structured entity data (Wikipedia-style).
    /// </summary>
    Infobox,
}
