namespace SearXSharp;

/// <summary>
/// Defines the time range filter for search results.
/// Maps to SearXNG's time_range parameter (day, week, month, year).
/// </summary>
public enum TimeRange
{
    /// <summary>
    /// Results from the past 24 hours.
    /// </summary>
    Day,

    /// <summary>
    /// Results from the past 7 days.
    /// </summary>
    Week,

    /// <summary>
    /// Results from the past 30 days.
    /// </summary>
    Month,

    /// <summary>
    /// Results from the past 365 days.
    /// </summary>
    Year,
}
