namespace SearXSharp;

/// <summary>
/// Represents information about an engine that did not respond or encountered an error.
/// Based on SearXNG's UnresponsiveEngine named tuple.
/// </summary>
/// <param name="Engine">The engine name.</param>
/// <param name="ErrorType">Type of error (e.g., "timeout", "HTTP 403", "CAPTCHA").</param>
/// <param name="IsSuspended">Whether the engine has been temporarily suspended due to errors.</param>
public readonly record struct UnresponsiveEngineInfo(string Engine, string ErrorType, bool IsSuspended);
