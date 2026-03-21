namespace Peak.Core.Models;

public class WeatherData
{
    public double Temperature { get; set; }
    public int WeatherCode { get; set; }
    public double WindSpeed { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string CityName { get; set; } = string.Empty;
}
