namespace DisneylandClient.ApiClient.Models;

/// <summary>
/// Sparkline record returned by <c>GET /sparklines/lightning-lane</c>.
/// <see cref="Buckets"/> contains 24 slots covering the last 120 minutes in
/// 5-minute increments, ordered oldest to newest.  A <see langword="null"/> slot
/// means no poll data was recorded for that interval; non-null slots have been
/// forward-filled by the backend so gaps only appear at the leading edge.
/// </summary>
public class SparklineDto
{
    public string EntityId      { get; set; } = string.Empty;
    public string Name          { get; set; } = string.Empty;
    public bool   IsLightningLane { get; set; }
    public int?[] Buckets       { get; set; } = [];
}

