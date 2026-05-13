using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Mastodon (mastodon.social).
/// Uses official Mastodon API v2 (no OAuth required for basic search).
/// Based on SearXNG's mastodon.py.
/// </summary>
public class MastodonSearchEngine : SearchEngineBase
{
    private const string _baseUrl = "https://mastodon.social";
    private const int _pageSize = 40;

    /// <summary>
    /// Type of Mastodon content to search: "accounts" or "hashtags".
    /// </summary>
    public string MastodonType { get; set; } = "accounts";

    /// <inheritdoc />
    public override string Name => "mastodon";

    /// <inheritdoc />
    public override string DisplayName => "Mastodon";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.SocialMedia };

    /// <inheritdoc />
    public override bool SupportsPaging => false;

    /// <inheritdoc />
    public override bool SupportsTimeRange => false;

    /// <inheritdoc />
    public override bool SupportsSafeSearch => false;

    /// <inheritdoc />
    public override int MaxPages => 1;

    /// <inheritdoc />
    public override double Timeout => 10.0;

    public MastodonSearchEngine() : base() { }
    public MastodonSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return CreateErrorResult("Query cannot be empty.");

        try
        {
            var args = new Dictionary<string, string>
            {
                ["q"] = query.Query,
                ["resolve"] = "false",
                ["type"] = MastodonType,
                ["limit"] = _pageSize.ToString(),
            };

            var url = $"{_baseUrl}/api/v2/search?" + string.Join("&", args.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(json);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResult("timeout", suspended: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed for query: {Query}", Name, query.Query);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private SearchResultList ParseJson(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty(MastodonType, out var items))
                return CreateResultList(results);

            foreach (var result in items.EnumerateArray())
            {
                try
                {
                    if (MastodonType == "accounts")
                    {
                        var uri = result.GetProperty("uri").GetString() ?? "";
                        var username = result.GetProperty("username").GetString() ?? "";

                        var followers = 0;
                        if (result.TryGetProperty("followers_count", out var fc))
                            followers = fc.GetInt32();

                        var note = "";
                        if (result.TryGetProperty("note", out var noteEl))
                            note = StripHtml(noteEl.GetString() ?? "");

                        var avatar = "";
                        if (result.TryGetProperty("avatar", out var av))
                            avatar = av.GetString() ?? "";

                        DateTime? created = null;
                        if (result.TryGetProperty("created_at", out var ca))
                        {
                            var dateStr = ca.GetString() ?? "";
                            if (dateStr.Length >= 10 && DateTime.TryParse(dateStr[..10], out var dt))
                                created = dt;
                        }

                        results.Add(new SearchResult
                        {
                            Url = uri,
                            Title = $"{username} ({followers} followers)",
                            Content = note,
                            Thumbnail = string.IsNullOrEmpty(avatar) ? null : avatar,
                            PublishedDate = created,
                            Engine = Name,
                            Category = SearchCategory.SocialMedia,
                        });
                    }
                    else if (MastodonType == "hashtags")
                    {
                        var name = result.GetProperty("name").GetString() ?? "";
                        var url = result.GetProperty("url").GetString() ?? "";

                        var totalUses = 0;
                        var totalUsers = 0;
                        if (result.TryGetProperty("history", out var history))
                        {
                            foreach (var h in history.EnumerateArray())
                            {
                                if (h.TryGetProperty("uses", out var uses))
                                    totalUses += int.Parse(uses.GetString() ?? "0");
                                if (h.TryGetProperty("accounts", out var accs))
                                    totalUsers += int.Parse(accs.GetString() ?? "0");
                            }
                        }

                        results.Add(new SearchResult
                        {
                            Url = url,
                            Title = name,
                            Content = $"Hashtag has been used {totalUses} times by {totalUsers} different users",
                            Engine = Name,
                            Category = SearchCategory.SocialMedia,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{Engine}: Failed to parse item", Name);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ").Trim();
    }
}
