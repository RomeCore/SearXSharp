using SearXSharp.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SearXSharp;

/// <summary>
/// Orchestrates multiple search engines, executes queries in parallel,
/// and merges/deduplicates results.
/// Based on SearXNG's SearchWithPlugins and ResultContainer architecture.
/// </summary>
public class SearchEngineManager
{
    private readonly List<ISearchEngine> _engines = new();
    private readonly ILogger _logger;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	public SearchEngineManager()
	{
        _logger = EmptyLogger.Instance;
	}

	/// <summary>
	/// Initializes a new instance with a logger.
	/// </summary>
	public SearchEngineManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the list of registered engines.
    /// </summary>
    public IReadOnlyList<ISearchEngine> Engines => _engines.AsReadOnly();

    /// <summary>
    /// Registers a search engine for use.
    /// </summary>
    public void RegisterEngine(ISearchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engines.Add(engine);
        _logger.Information("Registered search engine: {Engine} (v{Type})", engine.Name, engine.GetType().Name);
    }

    /// <summary>
    /// Registers multiple search engines at once.
    /// </summary>
    public void RegisterEngines(IEnumerable<ISearchEngine> engines)
    {
        foreach (var engine in engines)
        {
            RegisterEngine(engine);
        }
    }

    /// <summary>
    /// Executes a search query across all registered engines in parallel,
    /// then merges and deduplicates the results.
    /// Based on SearXNG's <c>Search.search_standard()</c> and <c>ResultContainer</c>.
    /// </summary>
    /// <param name="query">The search query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A merged and deduplicated search result list.</returns>
    public async Task<SearchResultList> SearchAllAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (_engines.Count == 0)
        {
            _logger.Warning("No search engines registered.");
            return new SearchResultList
            {
                Results = Array.Empty<SearchResult>(),
            };
        }

        _logger.Information("Searching across {Count} engines for: {Query}", _engines.Count, query.Query);

        var sw = Stopwatch.StartNew();
        var engineTasks = new List<Task<SearchResultList>>();
        var engineTimings = new ConcurrentBag<EngineTiming>();
        var unresponsiveEngines = new ConcurrentBag<UnresponsiveEngineInfo>();

        // Fire all engine requests in parallel (like SearXNG's threading approach)
        foreach (var engine in _engines)
        {
            engineTasks.Add(ExecuteEngineSearchAsync(engine, query, engineTimings, unresponsiveEngines, ct));
        }

        await Task.WhenAll(engineTasks);

        // Merge and deduplicate results
        var mergedResults = MergeResults(engineTasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result.Results));
        var suggestions = MergeStringLists(engineTasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result.Suggestions));
        var corrections = MergeStringLists(engineTasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result.Corrections));
        var infoboxes = engineTasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result.Infoboxes).ToList();
        var answers = engineTasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result.Answers).ToList();

        sw.Stop();
        _logger.Information("Search completed in {ElapsedMs}ms. Got {Count} results from {EngineCount} engines.",
            sw.ElapsedMilliseconds, mergedResults.Count, _engines.Count);

        return new SearchResultList
        {
            Results = mergedResults,
            Suggestions = suggestions,
            Corrections = corrections,
            Infoboxes = infoboxes,
            Answers = answers,
            TotalResults = mergedResults.Count,
            SupportsPaging = _engines.Any(e => e.SupportsPaging),
            EngineTimings = engineTimings.ToList(),
            UnresponsiveEngines = unresponsiveEngines.ToList(),
        };
    }

    /// <summary>
    /// Executes a search on a single engine, recording timings and errors.
    /// Based on SearXNG's <c>OnlineProcessor.search()</c> method.
    /// </summary>
    private async Task<SearchResultList> ExecuteEngineSearchAsync(
        ISearchEngine engine,
        SearchQuery query,
        ConcurrentBag<EngineTiming> timings,
        ConcurrentBag<UnresponsiveEngineInfo> unresponsive,
        CancellationToken ct)
    {
        var engineSw = Stopwatch.StartNew();
        try
        {
            var result = await engine.SearchAsync(query, ct);
            engineSw.Stop();

            timings.Add(new EngineTiming(engine.Name, engineSw.ElapsedMilliseconds, 0));

            _logger.Debug("Engine {Engine} returned {Count} results in {ElapsedMs}ms",
                engine.Name, result.Results.Count, engineSw.ElapsedMilliseconds);

            return result;
        }
        catch (TaskCanceledException)
        {
            engineSw.Stop();
            timings.Add(new EngineTiming(engine.Name, engineSw.ElapsedMilliseconds, 0));
            unresponsive.Add(new UnresponsiveEngineInfo(engine.Name, "timeout", true));
            _logger.Warning("Engine {Engine} timed out after {ElapsedMs}ms", engine.Name, engineSw.ElapsedMilliseconds);
            return CreateEmptyResult();
        }
        catch (Exception ex)
        {
            engineSw.Stop();
            timings.Add(new EngineTiming(engine.Name, engineSw.ElapsedMilliseconds, 0));
            unresponsive.Add(new UnresponsiveEngineInfo(engine.Name, ex.GetType().Name, false));
            _logger.Error(ex, "Engine {Engine} failed after {ElapsedMs}ms", engine.Name, engineSw.ElapsedMilliseconds);
            return CreateEmptyResult();
        }
    }

    /// <summary>
    /// Merges results from multiple engines, deduplicating by URL.
    /// Based on SearXNG's <c>ResultContainer._merge_main_result()</c>.
    /// </summary>
    private static List<SearchResult> MergeResults(IEnumerable<SearchResult> results)
    {
        var dedupMap = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            var key = result.GetDeduplicationKey();

            if (dedupMap.TryGetValue(key, out var existing))
            {
                // Merge: keep the result with more content
                if (result.Content.Length > existing.Content.Length)
                {
                    existing.Content = result.Content;
                }
                if (result.Title.Length > existing.Title.Length)
                {
                    existing.Title = result.Title;
                }
                existing.Engines.Add(result.Engine);
            }
            else
            {
                dedupMap[key] = result;
            }
        }

        // Sort by position and return
        return dedupMap.Values
            .OrderBy(r => r.Position)
            .ThenByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>
    /// Merges multiple string lists into one, removing duplicates while preserving order.
    /// </summary>
    private static List<string> MergeStringLists(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var item in items)
        {
            if (seen.Add(item))
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// Creates an empty result list.
    /// </summary>
    private static SearchResultList CreateEmptyResult()
    {
        return new SearchResultList
        {
            Results = Array.Empty<SearchResult>(),
        };
    }
}
