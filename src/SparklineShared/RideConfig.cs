namespace SparklineShared;

/// <summary>
/// Centralized ride configuration shared across the ThemeParkPoller,
/// CurrentStateCollector, and ThemeParkApi projects.
/// </summary>
public static class RideConfig
{
    /// <summary>
    /// Entity IDs of high-interest attractions that do not offer Lightning Lane
    /// but should be treated as included alongside LL rides in data collection
    /// and API responses.
    ///
    /// Values must match the <c>id</c> field returned by the themeparks.wiki API
    /// in the live-data response (i.e. the <c>EntityId</c> stored in DynamoDB).
    /// For Disneyland Resort attractions this is a UUID
    /// (e.g. <c>"82aeb29b-504a-416f-b13f-f41fa5b766aa"</c>) — verify against a live
    /// API call when adding new entries.
    ///
    /// Comparisons are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> SpecialInterestEntityIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "82aeb29b-504a-416f-b13f-f41fa5b766aa",
        };
}
