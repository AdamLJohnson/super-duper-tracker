using Refit;
using DisneylandClient.ApiClient.Models;

namespace DisneylandClient.ApiClient;

public interface IThemeParksApi
{
    /// <summary>
    /// Returns all Disneyland Resort attractions that currently have an active
    /// Lightning Lane slot, pre-filtered and enriched by the ThemeParkApi service.
    /// </summary>
    [Get("/attractions/lightning-lane")]
    Task<List<AttractionDto>> GetLightningLaneAttractionsAsync();

    /// <summary>
    /// Returns all Disneyland Resort attractions whose live status matches
    /// <paramref name="status"/> (e.g. <c>"OPERATING"</c>, <c>"DOWN"</c>).
    /// </summary>
    [Get("/attractions/status/{status}")]
    Task<List<AttractionDto>> GetAttractionsByStatusAsync(string status);

    /// <summary>
    /// Returns the current 24-bucket sparkline series for every Lightning Lane
    /// attraction.  Call this once on load so cards render trend lines immediately,
    /// before any <c>sparkline_updated</c> WebSocket events arrive.
    /// </summary>
    [Get("/sparklines/lightning-lane")]
    Task<List<SparklineDto>> GetLightningLaneSparklinesAsync();
}

