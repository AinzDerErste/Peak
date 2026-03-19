using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Notch.Core.Models;

namespace Notch.Core.Services;

public class WeatherService
{
    private readonly HttpClient _httpClient;

    public event Action<WeatherData>? WeatherUpdated;
    public WeatherData? CurrentWeather { get; private set; }

    public WeatherService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Weather");
    }

    /// <summary>
    /// Resolve postal code to coordinates, then fetch weather.
    /// Priority: PostalCode > IP geolocation > manual lat/lon fallback.
    /// </summary>
    public async Task<WeatherData?> FetchSmartAsync(string postalCode, string countryCode, double fallbackLat, double fallbackLon)
    {
        string cityName = "";

        // 1. Try postal code geocoding via Open-Meteo Geocoding API
        if (!string.IsNullOrWhiteSpace(postalCode))
        {
            try
            {
                var query = Uri.EscapeDataString($"{postalCode} {countryCode}");
                var url = $"https://geocoding-api.open-meteo.com/v1/search?name={query}&count=1&language=de";
                var geo = await _httpClient.GetFromJsonAsync<GeocodingResponse>(url);
                if (geo?.Results is { Count: > 0 })
                {
                    cityName = geo.Results[0].Name;
                    var w = await FetchAsync(geo.Results[0].Latitude, geo.Results[0].Longitude);
                    if (w != null) w.CityName = cityName;
                    return w;
                }
            }
            catch { /* geocoding failed */ }

            // Fallback: try zippopotam.us for precise postal code lookup
            try
            {
                var url = $"https://api.zippopotam.us/{countryCode.ToLower()}/{postalCode}";
                var zip = await _httpClient.GetFromJsonAsync<ZipResponse>(url);
                if (zip?.Places is { Count: > 0 })
                {
                    cityName = zip.Places[0].PlaceName;
                    var w = await FetchAsync(
                        double.Parse(zip.Places[0].Latitude, System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(zip.Places[0].Longitude, System.Globalization.CultureInfo.InvariantCulture));
                    if (w != null) w.CityName = cityName;
                    return w;
                }
            }
            catch { /* zip lookup failed */ }
        }

        // 2. Fallback: IP geolocation
        try
        {
            var geo = await _httpClient.GetFromJsonAsync<GeoIpResponse>("http://ip-api.com/json/?fields=lat,lon,city,status");
            if (geo is { Status: "success" })
            {
                cityName = geo.City;
                var w = await FetchAsync(geo.Lat, geo.Lon);
                if (w != null) w.CityName = cityName;
                return w;
            }
        }
        catch { /* geolocation failed */ }

        // 3. Last resort: manual coordinates
        return await FetchAsync(fallbackLat, fallbackLon);
    }

    public async Task<WeatherData?> FetchAsync(double lat, double lon)
    {
        try
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true";
            var response = await _httpClient.GetFromJsonAsync<OpenMeteoResponse>(url);
            if (response?.CurrentWeather == null) return null;

            var weather = new WeatherData
            {
                Temperature = response.CurrentWeather.Temperature,
                WeatherCode = response.CurrentWeather.Weathercode,
                WindSpeed = response.CurrentWeather.Windspeed,
                Description = GetWeatherDescription(response.CurrentWeather.Weathercode),
                Icon = GetWeatherIcon(response.CurrentWeather.Weathercode)
            };

            CurrentWeather = weather;
            WeatherUpdated?.Invoke(weather);
            return weather;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWeatherDescription(int code) => code switch
    {
        0 => "Clear",
        1 or 2 or 3 => "Partly Cloudy",
        45 or 48 => "Foggy",
        51 or 53 or 55 => "Drizzle",
        61 or 63 or 65 => "Rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow Grains",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow Showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm + Hail",
        _ => "Unknown"
    };

    private static string GetWeatherIcon(int code) => code switch
    {
        0 => "\u2600",       // ☀
        1 or 2 or 3 => "\u26C5", // ⛅
        45 or 48 => "\ud83c\udf2b", // 🌫
        51 or 53 or 55 or 61 or 63 or 65 or 80 or 81 or 82 => "\ud83c\udf27", // 🌧
        71 or 73 or 75 or 77 or 85 or 86 => "\u2744",  // ❄
        95 or 96 or 99 => "\u26A1", // ⚡
        _ => "\u2601"        // ☁
    };

    private class OpenMeteoResponse
    {
        [JsonPropertyName("current_weather")]
        public CurrentWeatherData? CurrentWeather { get; set; }
    }

    private class CurrentWeatherData
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("weathercode")]
        public int Weathercode { get; set; }

        [JsonPropertyName("windspeed")]
        public double Windspeed { get; set; }
    }

    private class GeocodingResponse
    {
        [JsonPropertyName("results")]
        public List<GeocodingResult>? Results { get; set; }
    }

    private class GeocodingResult
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private class ZipResponse
    {
        [JsonPropertyName("places")]
        public List<ZipPlace>? Places { get; set; }
    }

    private class ZipPlace
    {
        [JsonPropertyName("latitude")]
        public string Latitude { get; set; } = "0";

        [JsonPropertyName("longitude")]
        public string Longitude { get; set; } = "0";

        [JsonPropertyName("place name")]
        public string PlaceName { get; set; } = "";
    }

    private class GeoIpResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }

        [JsonPropertyName("city")]
        public string City { get; set; } = "";
    }
}
