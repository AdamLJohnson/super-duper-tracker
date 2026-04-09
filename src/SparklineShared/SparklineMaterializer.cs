using Amazon.DynamoDBv2.Model;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SparklineShared;

/// <summary>
/// Shared constants and stateless helpers for building and reading sparkline data.
/// Used by both <c>SparklineProcessor</c> and <c>ThemeParkApi</c>.
/// </summary>
public static class SparklineMaterializer
{
    public const int BucketMinutes = 5;
    public const int WindowMinutes = 120;
    public const int BucketCount   = WindowMinutes / BucketMinutes; // 24

    /// <summary>Shared JSON serializer options used across all sparkline serialization.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Floors a <see cref="DateTimeOffset"/> to the nearest <see cref="BucketMinutes"/>-minute
    /// boundary so all updates that arrive within the same bucket are aggregated together.
    /// </summary>
    public static DateTimeOffset FloorToBucket(DateTimeOffset ts)
    {
        var totalMinutes = (long)ts.ToUniversalTime().TimeOfDay.TotalMinutes;
        var floored      = totalMinutes - (totalMinutes % BucketMinutes);
        return new DateTimeOffset(ts.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(floored);
    }

    /// <summary>
    /// Projects the sparse bucket dictionary onto a fixed-length array of
    /// <see cref="BucketCount"/> slots (oldest → newest), then forward-fills any
    /// null gap with the most recent preceding known value. The forward-fill is seeded
    /// with <paramref name="seedValue"/> — the oldest bucket value captured from the
    /// sparse series <em>before</em> stale entries were pruned — so that leading null
    /// slots are filled with a historically accurate value even after a long update gap.
    /// Slots before any recorded observation remain null when no seed is available.
    /// </summary>
    public static int?[] Materialize(Dictionary<DateTimeOffset, int> sparse, int? seedValue)
    {
        var newestBucket = FloorToBucket(DateTimeOffset.UtcNow);
        var result       = new int?[BucketCount];

        for (int i = 0; i < BucketCount; i++)
        {
            var slot = newestBucket.AddMinutes(-(BucketCount - 1 - i) * BucketMinutes);
            result[i] = sparse.TryGetValue(slot, out var v) ? v : null;
        }

        // Seed the forward-fill with the oldest bucket value captured before pruning,
        // so that leading null slots are filled with the historically accurate value
        // even when a long gap caused all prior buckets to be pruned away.
        int? lastKnown = seedValue;

        // Forward-fill: carry the last known value into null slots so a missed
        // poll cycle does not produce a visual gap in the sparkline.
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] is not null)
                lastKnown = result[i];
            else if (lastKnown is not null)
                result[i] = lastKnown;
        }

        return result;
    }

    /// <summary>
    /// Reads the <c>SeedValue</c> number attribute from a DynamoDB item dictionary.
    /// Returns <c>null</c> if the attribute is absent or cannot be parsed as an integer.
    /// </summary>
    public static int? ParseSeedValue(Dictionary<string, AttributeValue> item)
    {
        if (item.TryGetValue("SeedValue", out var attr) &&
            attr.N is not null &&
            int.TryParse(attr.N, out var value))
            return value;
        return null;
    }

    /// <summary>
    /// Deserializes a <c>BucketsJson</c> string into a sparse bucket dictionary.
    /// Throws <see cref="JsonException"/> if <paramref name="json"/> is not valid JSON.
    /// </summary>
    public static Dictionary<DateTimeOffset, int> ParseBucketsJson(string json)
    {
        var stored = JsonSerializer.Deserialize<List<StoredBucket>>(json, JsonOptions) ?? [];
        return stored.ToDictionary(b => b.T, b => b.V);
    }
}

