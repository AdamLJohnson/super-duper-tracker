namespace SparklineShared;

/// <summary>
/// Payload for a sparkline record — used in both the <c>sparkline_updated</c> SNS event
/// and the <c>GET /sparklines/lightning-lane</c> REST response.
/// <see cref="Buckets"/> has <see cref="SparklineMaterializer.BucketCount"/> slots
/// (oldest → newest, 5-min steps). Null slots mean no data was recorded for that interval.
/// </summary>
public sealed record SparklineDto(
    string EntityId,
    string Name,
    bool   IsLightningLane,
    int?[] Buckets);

