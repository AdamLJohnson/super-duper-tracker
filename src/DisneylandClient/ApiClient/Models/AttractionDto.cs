namespace DisneylandClient.ApiClient.Models;

/// <summary>
/// Attraction record returned by the ThemeParkApi
/// <c>GET /attractions/lightning-lane</c> and <c>GET /attractions/status/{status}</c> endpoints.
/// </summary>
public class AttractionDto
{
    /// <summary>Unique entity identifier (DynamoDB partition key).</summary>
    public string EntityId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Live operational status string (e.g. <c>"OPERATING"</c>, <c>"DOWN"</c>,
    /// <c>"CLOSED"</c>, <c>"REFURBISHMENT"</c>). <see langword="null"/> when unknown.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>The Lightning Lane return-window start time, if a slot is open.</summary>
    public DateTimeOffset? LightningLaneTime { get; set; }

    /// <summary>
    /// Lightning Lane availability state string (e.g. <c>"AVAILABLE"</c>,
    /// <c>"TEMP_FULL"</c>, <c>"FINISHED"</c>). <see langword="null"/> when the
    /// attraction has no active Lightning Lane slot.
    /// </summary>
    public string? LightningLaneStatus { get; set; }

    /// <summary>Current standby queue wait time in minutes. <see langword="null"/> when unavailable.</summary>
    public int? StandbyWaitTime { get; set; }

    public DateTimeOffset LastUpdated { get; set; }
    public bool IsLightningLane { get; set; }
}

