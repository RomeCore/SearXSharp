using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for wttr.in (weather forecast service).
/// Uses the wttr.in JSON API (no key required).
/// Based on SearXNG's wttr.py.
/// </summary>
public class WttrSearchEngine : SearchEngineBase
{
    private const string _url = "https://wttr.in/{query}?format=j1";

    /// <inheritdoc />
    public override string Name => "wttr";

    /// <inheritdoc />
    public override string DisplayName => "wttr.in";

    /// <inheritdoc />
    public override IReadOnlyList<SearchCategory> SupportedCategories { get; }
        = new[] { SearchCategory.General };

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

    public WttrSearchEngine() : base() { }
    public WttrSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            var url = _url.Replace("{query}", Uri.EscapeDataString(query.Query));

            using var request = CreateGetRequest(url);
            var response = await SendRequestAsync(request, ct);

            if ((int)response.StatusCode == 404)
                return CreateResultList(new List<SearchResult>());

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJson(json, query.Query);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResult("timeout", suspended: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Engine}: Search failed", Name);
            return CreateErrorResult(ex.GetType().Name);
        }
    }

    private static readonly Dictionary<string, string> _weatherCodes = new()
    {
        ["113"] = "Clear sky",
        ["116"] = "Partly cloudy",
        ["119"] = "Cloudy",
        ["122"] = "Fair",
        ["176"] = "Light rain showers",
        ["200"] = "Rain and thunder",
        ["227"] = "Light snow",
        ["230"] = "Heavy snow",
        ["248"] = "Fog",
        ["260"] = "Fog",
        ["263"] = "Light rain showers",
        ["266"] = "Light rain",
        ["293"] = "Light rain showers",
        ["296"] = "Light rain",
        ["299"] = "Rain showers",
        ["302"] = "Rain",
        ["305"] = "Heavy rain showers",
        ["308"] = "Heavy rain",
        ["311"] = "Light sleet",
        ["314"] = "Sleet",
        ["320"] = "Heavy sleet",
        ["323"] = "Light snow showers",
        ["326"] = "Light snow showers",
        ["329"] = "Heavy snow showers",
        ["332"] = "Heavy snow",
        ["335"] = "Heavy snow showers",
        ["338"] = "Heavy snow",
        ["350"] = "Light sleet",
        ["353"] = "Light rain showers",
        ["356"] = "Heavy rain showers",
        ["359"] = "Heavy rain",
        ["362"] = "Light sleet showers",
        ["365"] = "Sleet showers",
        ["368"] = "Light snow showers",
        ["371"] = "Heavy snow showers",
        ["374"] = "Light sleet showers",
        ["377"] = "Heavy sleet",
        ["386"] = "Rain showers and thunder",
        ["389"] = "Heavy rain showers and thunder",
        ["392"] = "Snow showers and thunder",
        ["395"] = "Heavy snow showers",
    };

    private SearchResultList ParseJson(string json, string query)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_condition", out var currentArr)
                || currentArr.GetArrayLength() == 0)
                return CreateResultList(results);

            var current = currentArr[0];

            var tempC = current.TryGetProperty("temp_C", out var tempEl)
                ? tempEl.GetString() ?? "" : "";
            var feelsLike = current.TryGetProperty("FeelsLikeC", out var feelsEl)
                ? feelsEl.GetString() ?? "" : "";
            var weatherCode = current.TryGetProperty("weatherCode", out var codeEl)
                ? codeEl.GetString() ?? "" : "";
            var windSpeed = current.TryGetProperty("windspeedKmph", out var windEl)
                ? windEl.GetString() ?? "" : "";
            var humidity = current.TryGetProperty("humidity", out var humEl)
                ? humEl.GetString() ?? "" : "";
            var pressure = current.TryGetProperty("pressure", out var pressEl)
                ? pressEl.GetString() ?? "" : "";
            var cloudCover = current.TryGetProperty("cloudcover", out var cloudEl)
                ? cloudEl.GetString() ?? "" : "";

            var condition = _weatherCodes.GetValueOrDefault(weatherCode, weatherCode);

            var content = $"🌡️ {tempC}°C (feels like {feelsLike}°C) | "
                        + $"🌤️ {condition} | "
                        + $"💨 {windSpeed} km/h | "
                        + $"💧 {humidity}% | "
                        + $"🔽 {pressure} hPa";

            if (!string.IsNullOrEmpty(cloudCover))
                content += $" | ☁️ {cloudCover}% cloud cover";

            // Also add forecast
            if (root.TryGetProperty("weather", out var weatherArr))
            {
                var forecastParts = new List<string>();
                var dayCount = 0;
                foreach (var day in weatherArr.EnumerateArray())
                {
                    if (dayCount >= 3) break;
                    var date = day.GetProperty("date").GetString() ?? "";
                    if (day.TryGetProperty("hourly", out var hourly) && hourly.GetArrayLength() > 0)
                    {
                        var mid = hourly[0];
                        var temp = mid.TryGetProperty("tempC", out var t) ? t.GetString() ?? "?" : "?";
                        var code = mid.TryGetProperty("weatherCode", out var c) ? c.GetString() ?? "" : "";
                        var cond = _weatherCodes.GetValueOrDefault(code, code);
                        forecastParts.Add($"{date}: {temp}°C {cond}");
                    }
                    dayCount++;
                }

                if (forecastParts.Count > 0)
                    content += $"\n\n📅 Forecast: {string.Join(" | ", forecastParts)}";
            }

            results.Add(new SearchResult
            {
                Url = $"https://wttr.in/{Uri.EscapeDataString(query)}",
                Title = $"Weather in {query}",
                Content = content,
                Engine = Name,
                Category = SearchCategory.General,
            });
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse JSON", Name);
        }

        return CreateResultList(results);
    }
}
