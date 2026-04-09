namespace SparklineShared;

/// <summary>One data point as stored in DynamoDB's BucketsJson attribute.</summary>
public sealed class StoredBucket
{
    public DateTimeOffset T { get; set; }
    public int            V { get; set; }
}

