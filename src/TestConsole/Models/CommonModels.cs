using System.Text.Json;

namespace DisneylandClient.Models;

public class PriceData
{
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? Formatted { get; set; }
}

public class Location
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public class TagData
{
    public string Tag { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string? Id { get; set; }

    /// <summary>
    /// Value can be a string, number, or object per the OpenAPI spec.
    /// </summary>
    public JsonElement? Value { get; set; }
}

