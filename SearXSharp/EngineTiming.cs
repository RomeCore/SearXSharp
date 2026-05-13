namespace SearXSharp;

/// <summary>
/// Represents timing information for a single engine request.
/// Based on SearXNG's Timing named tuple used in ResultContainer.
/// </summary>
/// <param name="Engine">The engine name.</param>
/// <param name="TotalTime">Total search duration in milliseconds.</param>
/// <param name="HttpLoadTime">HTTP request/response duration in milliseconds.</param>
public readonly record struct EngineTiming(string Engine, double TotalTime, double HttpLoadTime);
