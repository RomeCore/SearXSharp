using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SearXSharp.Models;

/// <summary>
/// Represents a single search result item returned by any search engine.
/// Based on SearXNG's MainResult class with support for multiple content types.
/// </summary>
[DebuggerDisplay("{Title} ({Url})")]
public partial class SearchResult
{
    /// <summary>
    /// The URL of the result.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// The title of the result.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// A textual extract or description of the result.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The name of the engine that produced this result.
    /// </summary>
    public string Engine { get; init; } = string.Empty;

    /// <summary>
    /// The template used for rendering (maps to SearXNG's template field).
    /// Defaults to "default.html".
    /// </summary>
    public string Template { get; init; } = "default.html";

    /// <summary>
    /// The score assigned to this result during ranking.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// The type of result (determines how it is processed and displayed).
    /// </summary>
    public SearchResultType Type { get; init; } = SearchResultType.Default;

    /// <summary>
    /// The search category this result belongs to.
    /// </summary>
    public SearchCategory Category { get; init; } = SearchCategory.General;

    // ——— Media fields ———

    /// <summary>
    /// URL of a thumbnail image for the result.
    /// </summary>
    public string? Thumbnail { get; init; }

    /// <summary>
    /// URL of a full-size image (for image search results).
    /// </summary>
    public string? ImgSrc { get; init; }

    /// <summary>
    /// URL for an embedded iframe (e.g., video player).
    /// </summary>
    public string? IframeSrc { get; init; }

    /// <summary>
    /// URL for embedded audio playback.
    /// </summary>
    public string? AudioSrc { get; init; }

    // ——— Time fields ———

    /// <summary>
    /// The publication date of the result.
    /// </summary>
    public DateTime? PublishedDate { get; init; }

    /// <summary>
    /// The duration of a media result (video, audio).
    /// </summary>
    public TimeSpan? Duration { get; init; }

    // ——— Metadata fields ———

    /// <summary>
    /// The author or creator of the result.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Miscellaneous metadata string.
    /// </summary>
    public string? Metadata { get; init; }

    /// <summary>
    /// The source name (e.g., news source, website name).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// The number of views (for videos, etc.).
    /// </summary>
    public long? Views { get; init; }

    /// <summary>
    /// Resolution string for images (e.g., "1920 x 1080").
    /// </summary>
    public string? Resolution { get; init; }

    // ——— Engine-specific data ———

    /// <summary>
    /// Set of engine names that found this result (after merging duplicates).
    /// </summary>
    public HashSet<string> Engines { get; init; } = [];

    /// <summary>
    /// The position of this result in the original engine response.
    /// </summary>
    public int Position { get; init; }

    // --- Torrent-specific fields ---

    /// <summary>
    /// The number of seeds for this torrent.
    /// </summary>
    public int Seed { get; init; } = 0;

    /// <summary>
    /// The number of peers that are currently seeding the torrent.
    /// </summary>
    public int Leech { get; init; } = 0;

    /// <summary>
    /// The magnet link for this torrent.
    /// </summary>
    public string? MagnetLink { get; init; } = null;

    // --- Geo-specific fields ———

    /// <summary>
    /// The latitude of the result.
    /// </summary>
    public double Latitude { get; init; } = 0.0;

    /// <summary>
    /// The longitude of the result.
    /// </summary>
    public double Longitude { get; init; } = 0.0;

    // ——— Paper-specific fields ———

    /// <summary>
    /// DOI identifier (for Paper results).
    /// </summary>
    public string? Doi { get; init; }

    /// <summary>
    /// List of authors (for Paper results).
    /// </summary>
    public IReadOnlyList<string>? Authors { get; init; }

    /// <summary>
    /// Journal name (for Paper results).
    /// </summary>
    public string? Journal { get; init; }

    /// <summary>
    /// Tags or keywords (for Paper results).
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Comments (for Paper results).
    /// </summary>
    public string? Comments { get; init; }

    /// <summary>
    /// PDF URL (for Paper results).
    /// </summary>
    public string? PdfUrl { get; init; }

	/// <summary>
	/// The number of pages in the paper.
	/// </summary>
	public string? Pages { get; init; } = null;

	/// <summary>
	/// The volume number of the journal.
	/// </summary>
	public string? Volume { get; init; } = null;

	/// <summary>
	/// The issue number of the journal.
	/// </summary>
	public string? Number { get; init; } = null;

	/// <summary>
	/// Generates a hash key for deduplication.
	/// Based on SearXNG's MainResult.__hash__ logic (template + netloc + path + query + img_src).
	/// </summary>
	public string GetDeduplicationKey()
    {
        try
        {
            var uri = new Uri(Url);
            return $"{Template}|{uri.Host}|{uri.AbsolutePath}|{uri.Query}|{ImgSrc ?? ""}";
        }
        catch
        {
            return $"{Template}|{Url}|{ImgSrc ?? ""}";
        }
    }

    /// <summary>
    /// Normalizes title and content: collapses whitespace, trims.
    /// Based on SearXNG's _normalize_text_fields().
    /// </summary>
    public void NormalizeText()
    {
        if (!string.IsNullOrEmpty(Title))
            Title = WhitespaceRegex().Replace(Title, " ").Trim();
        if (!string.IsNullOrEmpty(Content))
            Content = WhitespaceRegex().Replace(Content, " ").Trim();
        if (Content == Title)
            Content = string.Empty;
    }

    [GeneratedRegex(@"( |\t|\n)+", RegexOptions.Multiline)]
    private static partial Regex WhitespaceRegex();
}
