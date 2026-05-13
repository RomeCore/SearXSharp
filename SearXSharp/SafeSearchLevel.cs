namespace SearXSharp;

/// <summary>
/// Defines the safe search filtering level.
/// Maps to SearXNG's safesearch parameter (0=None, 1=Moderate, 2=Strict).
/// </summary>
public enum SafeSearchLevel
{
    /// <summary>
    /// No filtering — all results are shown.
    /// </summary>
    None = 0,

    /// <summary>
    /// Moderate filtering — explicit content is blurred or hidden.
    /// </summary>
    Moderate = 1,

    /// <summary>
    /// Strict filtering — explicit content is completely removed.
    /// </summary>
    Strict = 2,
}
