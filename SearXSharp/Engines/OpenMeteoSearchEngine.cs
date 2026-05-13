using SearXSharp.Models;
using System.Text.Json;

namespace SearXSharp.Engines;

/// <summary>
/// Search engine implementation for Open-Meteo (open-meteo.com).
/// Free weather forecast API (no key required).
/// Based on SearXNG's open_meteo.py.
/// </summary>
public class OpenMeteoSearchEngine : SearchEngineBase
{
    private const string _geoUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string _apiUrl = "https://api.open-meteo.com/v1/forecast";

    private static readonly Dictionary<int, string> _wmoCodes = new()
    {
        [0] = "Clear sky",
        [1] = "Fair",
        [2] = "Partly cloudy",
        [3] = "Cloudy",
        [45] = "Fog",
        [48] = "Fog",
        [51] = "Light rain",
        [53] = "Light rain",
        [55] = "Light rain",
        [56] = "Light sleet showers",
        [57] = "Light sleet",
        [61] = "Light rain",
        [63] = "Rain",
        [65] = "Heavy rain",
        [66] = "Light sleet showers",
        [67] = "Light sleet",
        [71] = "Light sleet",
        [73] = "Sleet",
        [75] = "Heavy sleet",
        [77] = "Snow",
        [80] = "Light rain showers",
        [81] = "Rain showers",
        [82] = "Heavy rain showers",
        [85] = "Snow showers",
        [86] = "Heavy snow showers",
        [95] = "Rain and thunder",
        [96] = "Light snow and thunder",
        [99] = "Heavy snow and thunder",
    };

    /// <inheritdoc />
    public override string Name => "openmeteo";

    /// <inheritdoc />
    public override string DisplayName => "Open-Meteo";

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
    public override double Timeout => 12.0;

    public OpenMeteoSearchEngine() : base() { }
    public OpenMeteoSearchEngine(ILogger logger) : base(logger) { }

    /// <inheritdoc />
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var validationError = ValidateQuery(query);
        if (validationError != null)
            return CreateErrorResult(validationError);

        try
        {
            // First: geocode the location
            var geoUrl = $"{_geoUrl}?name={Uri.EscapeDataString(query.Query)}&count=1&format=json";
            using var geoRequest = CreateGetRequest(geoUrl);
            var geoResponse = await SendRequestAsync(geoRequest, ct);
            geoResponse.EnsureSuccessStatusCode();

            var geoJson = await geoResponse.Content.ReadAsStringAsync(ct);
            using var geoDoc = JsonDocument.Parse(geoJson);
            var geoRoot = geoDoc.RootElement;

            if (!geoRoot.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return CreateResultList(new List<SearchResult>());

            var location = results[0];
            var lat = location.GetProperty("latitude").GetDouble();
            var lon = location.GetProperty("longitude").GetDouble();
            var name = location.GetProperty("name").GetString() ?? query.Query;
            var country = location.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";

            // Second: get weather forecast
            var dataFields = new[] { "temperature_2m", "apparent_temperature", "relative_humidity_2m",
                                      "cloud_cover", "pressure_msl", "wind_speed_10m",
                                      "wind_direction_10m", "weather_code" };
            var forecastUrl = $"{_apiUrl}/v1/forecast?latitude={lat}&longitude={lon}" +
                $"&current={string.Join(",", dataFields)}" +
                "&timeformat=unixtime&timezone=auto&forecast_days=3" +
                $"&hourly={string.Join(",", dataFields)}";

            using var forecastRequest = CreateGetRequest(forecastUrl);
            var forecastResponse = await SendRequestAsync(forecastRequest, ct);
            forecastResponse.EnsureSuccessStatusCode();

            var forecastJson = await forecastResponse.Content.ReadAsStringAsync(ct);
            return ParseForecast(forecastJson, name, country);
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

    private SearchResultList ParseForecast(string json, string name, string country)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current", out var current))
                return CreateResultList(new List<SearchResult>());

            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feelsLike = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetDouble();
            var cloudCover = current.GetProperty("cloud_cover").GetDouble();
            var pressure = current.GetProperty("pressure_msl").GetDouble();
            var windSpeed = current.GetProperty("wind_speed_10m").GetDouble();
            var windDir = current.GetProperty("wind_direction_10m").GetDouble();
            var weatherCode = current.GetProperty("weather_code").GetInt32();

            var condition = _wmoCodes.GetValueOrDefault(weatherCode, $"Code {weatherCode}");
            var windDirStr = windDir switch
            {
                >= 337.5 or < 22.5 => "N",
                >= 22.5 and < 67.5 => "NE",
                >= 67.5 and < 112.5 => "E",
                >= 112.5 and < 157.5 => "SE",
                >= 157.5 and < 202.5 => "S",
                >= 202.5 and < 247.5 => "SW",
                >= 247.5 and < 292.5 => "W",
                _ => "NW"
            };

            var locationStr = string.IsNullOrEmpty(country) ? name : $"{name}, {country}";
            var content = $"📍 {locationStr}\n"
                        + $"🌡️ {temp:F1}°C (feels like {feelsLike:F1}°C)\n"
                        + $"🌤️ {condition}\n"
                        + $"💨 {windSpeed:F1} km/h ({windDirStr})\n"
                        + $"💧 {humidity:F0}%\n"
                        + $"🔽 {pressure:F0} hPa\n"
                        + $"☁️ {cloudCover:F0}% cloud cover";

            // Add forecast
            if (root.TryGetProperty("hourly", out var hourly))
            {
                var times = hourly.GetProperty("time");
                var temps = hourly.GetProperty("temperature_2m");
                var codes = hourly.GetProperty("weather_code");

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var forecastParts = new List<string>();
                var count = 0;

                for (int i = 0; i < times.GetArrayLength() && count < 4; i++)
                {
                    var time = times[i].GetInt64();
                    if (time > now)
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(time).DateTime;
                        var ft = temps[i].GetDouble();
                        var wc = codes[i].GetInt32();
                        var cond = _wmoCodes.GetValueOrDefault(wc, $"?");
                        forecastParts.Add($"{dt:ddd HH:mm}: {ft:F1}°C {cond}");
                        count++;
                    }
                }

                if (forecastParts.Count > 0)
                    content += "\n\n📅 Forecast:\n" + string.Join("\n", forecastParts);
            }

            return CreateResultList(new List<SearchResult>
            {
                new()
                {
                    Url = $"https://open-meteo.com/?lat={Uri.EscapeDataString(name)}",
                    Title = $"Weather in {locationStr}",
                    Content = content,
                    Engine = Name,
                    Category = SearchCategory.General,
                }
            });
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "{Engine}: Failed to parse forecast JSON", Name);
            return CreateResultList(new List<SearchResult>());
        }
    }
}
