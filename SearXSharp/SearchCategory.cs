namespace SearXSharp;

/// <summary>
/// Defines the search categories that engines can support.
/// Maps to SearXNG's category system used in settings.yml and engine classification.
/// </summary>
public enum SearchCategory
{
    /// <summary>
    /// General web search (catch-all category).
    /// </summary>
    General,

    /// <summary>
    /// Web pages (default for most engines).
    /// </summary>
    Web,

    /// <summary>
    /// Image search results.
    /// </summary>
    Images,

    /// <summary>
    /// Video search results.
    /// </summary>
    Videos,

    /// <summary>
    /// News articles.
    /// </summary>
    News,

    /// <summary>
    /// Scientific publications and academic content.
    /// </summary>
    Science,

    /// <summary>
    /// Information technology (repos, code, etc.).
    /// </summary>
    IT,

    /// <summary>
    /// File hosting and downloadable content.
    /// </summary>
    Files,

    /// <summary>
    /// Social media content.
    /// </summary>
    SocialMedia,

    /// <summary>
    /// Music and audio content.
    /// </summary>
    Music,

    /// <summary>
    /// Maps and geographic content.
    /// </summary>
    Map,

	/// <summary>
	/// Code repositories (GitHub, GitLab, etc.).
	/// </summary>
	Repos,

	/// <summary>
	/// Software packages and registries (npm, PyPI, Docker Hub, etc.).
	/// </summary>
	Packages,
}
