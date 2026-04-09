using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SNSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using System.Text.Json;
using SparklineShared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SparklineProcessor;

/// <summary>
/// Maintains a 24-point / 5-minute-bucket sparkline for every Lightning Lane attraction.
/// Triggered by <c>attraction_updated</c> SNS events; publishes a <c>sparkline_updated</c>
/// event back to the same topic so the WebSocket broadcaster can fan it out to clients.
/// </summary>
public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _sparklineTableName;
    private readonly string _updatesTopicArn;

    /// <summary>Parameterless constructor used by the Lambda runtime.</summary>
    public Function()
    {
        _dynamoDb           = new AmazonDynamoDBClient();
        _sns                = new AmazonSimpleNotificationServiceClient();
        _sparklineTableName = GetRequiredEnv("SPARKLINE_TABLE_NAME");
        _updatesTopicArn    = GetRequiredEnv("UPDATES_TOPIC_ARN");
    }

    /// <summary>Constructor that accepts pre-built clients (for unit testing).</summary>
    public Function(
        IAmazonDynamoDB dynamoDb,
        IAmazonSimpleNotificationService sns,
        string sparklineTableName,
        string updatesTopicArn)
    {
        _dynamoDb           = dynamoDb;
        _sns                = sns;
        _sparklineTableName = sparklineTableName;
        _updatesTopicArn    = updatesTopicArn;
    }

    public async Task FunctionHandler(SNSEvent snsEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"[Sparkline] Processing {snsEvent.Records.Count} SNS record(s).");
        foreach (var record in snsEvent.Records)
            await ProcessRecordAsync(record, context);
    }

    private async Task ProcessRecordAsync(SNSEvent.SNSRecord record, ILambdaContext context)
    {
        AttractionUpdateDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<AttractionUpdateDto>(record.Sns.Message, SparklineMaterializer.JsonOptions)
                  ?? throw new InvalidOperationException("Deserialized DTO was null.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"[Sparkline] Failed to deserialize SNS message: {ex.Message}");
            return;
        }

        // Resolve the bucket value from the incoming update:
        //   • Non-OPERATING status (DOWN, CLOSED, REFURBISHMENT, …) → 0, so the
        //     sparkline shows a zero-baseline instead of a gap during downtime.
        //   • OPERATING with a known wait time                       → that wait time.
        //   • OPERATING with a null wait time                        → skip; we have
        //     no meaningful value to record and a gap is preferable to a false zero.
        int bucketValue;
        if (dto.Status != "OPERATING")
        {
            bucketValue = 0;
            context.Logger.LogInformation(
                $"[Sparkline] '{dto.EntityId}' is {dto.Status ?? "unknown"} — recording 0 for this bucket.");
        }
        else if (dto.StandbyWaitTime is int waitTime)
        {
            bucketValue = waitTime;
        }
        else
        {
            context.Logger.LogInformation(
                $"[Sparkline] Skipping '{dto.EntityId}' — OPERATING but StandbyWaitTime is null.");
            return;
        }

        var bucketTime = SparklineMaterializer.FloorToBucket(dto.LastUpdated);
        var cutoff     = SparklineMaterializer.FloorToBucket(DateTimeOffset.UtcNow).AddMinutes(-(SparklineMaterializer.WindowMinutes - SparklineMaterializer.BucketMinutes));

        // Read current sparse series, upsert the new bucket.
        var sparse = await LoadSparseBucketsAsync(dto.EntityId, context);
        sparse[bucketTime] = bucketValue;

        // Capture the oldest known value BEFORE pruning stale entries.
        // This becomes the seed for the forward-fill pass so that leading null slots
        // in the materialized sparkline are filled with the historically accurate value
        // even when a long gap causes all prior buckets to be removed by the prune.
        int? seedValue = sparse.Count > 0 ? sparse.MinBy(kv => kv.Key).Value : null;

        foreach (var key in sparse.Keys.Where(k => k < cutoff).ToList())
            sparse.Remove(key);

        await PersistSparklineAsync(dto, sparse, seedValue, context);

        var buckets = SparklineMaterializer.Materialize(sparse, seedValue);
        await PublishSparklineUpdatedAsync(dto, buckets, context);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the existing sparse bucket series for <paramref name="entityId"/> from DynamoDB.
    /// Returns an empty dictionary if no record exists yet.
    /// </summary>
    private async Task<Dictionary<DateTimeOffset, int>> LoadSparseBucketsAsync(
        string entityId, ILambdaContext context)
    {
        try
        {
            var response = await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = _sparklineTableName,
                Key       = new Dictionary<string, AttributeValue>
                {
                    ["EntityId"] = new AttributeValue { S = entityId },
                },
                ProjectionExpression = "BucketsJson",
            });

            if (!response.IsItemSet ||
                !response.Item.TryGetValue("BucketsJson", out var attr) ||
                attr.S is null)
                return [];

            return SparklineMaterializer.ParseBucketsJson(attr.S);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"[Sparkline] DynamoDB GetItem failed for '{entityId}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Writes the updated sparse bucket series back to <c>SparklineStore</c>.
    /// Also keeps <c>IsLightningLane</c> and <c>Name</c> current for GSI queries.
    /// </summary>
    private async Task PersistSparklineAsync(
        AttractionUpdateDto dto,
        Dictionary<DateTimeOffset, int> sparse,
        int? seedValue,
        ILambdaContext context)
    {
        var bucketsJson = JsonSerializer.Serialize(
            sparse.OrderBy(kv => kv.Key)
                  .Select(kv => new StoredBucket { T = kv.Key, V = kv.Value })
                  .ToList(),
            SparklineMaterializer.JsonOptions);

        try
        {
            await _dynamoDb.PutItemAsync(_sparklineTableName, new Dictionary<string, AttributeValue>
            {
                ["EntityId"]        = new AttributeValue { S = dto.EntityId },
                ["IsLightningLane"] = new AttributeValue { S = dto.IsLightningLane ? "true" : "false" },
                ["Name"]            = new AttributeValue { S = dto.Name },
                ["BucketsJson"]     = new AttributeValue { S = bucketsJson },
                ["SeedValue"]       = new AttributeValue { N = (seedValue ?? 0).ToString() },
                ["LastUpdated"]     = new AttributeValue { S = dto.LastUpdated.ToString("O") },
            });
            context.Logger.LogInformation(
                $"[Sparkline] Persisted {sparse.Count} bucket(s) for '{dto.EntityId}'.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"[Sparkline] DynamoDB PutItem failed for '{dto.EntityId}': {ex.Message}");
        }
    }

    /// <summary>Publishes a <c>sparkline_updated</c> notification to the UpdatesTopic.</summary>
    private async Task PublishSparklineUpdatedAsync(
        AttractionUpdateDto dto, int?[] buckets, ILambdaContext context)
    {
        var payload = JsonSerializer.Serialize(new SparklineDto(
            EntityId:        dto.EntityId,
            Name:            dto.Name,
            IsLightningLane: dto.IsLightningLane,
            Buckets:         buckets), SparklineMaterializer.JsonOptions);

        try
        {
            var response = await _sns.PublishAsync(new PublishRequest
            {
                TopicArn = _updatesTopicArn,
                Message  = payload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["event_type"] = new MessageAttributeValue
                    {
                        DataType    = "String",
                        StringValue = "sparkline_updated",
                    },
                },
            });
            context.Logger.LogInformation(
                $"[Sparkline] Published sparkline_updated for '{dto.EntityId}'. MessageId: {response.MessageId}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"[Sparkline] Failed to publish SNS for '{dto.EntityId}': {ex.Message}");
        }
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            Console.Error.WriteLine($"[Function] Missing required environment variable: {name}");
            throw new InvalidOperationException($"{name} environment variable is not set.");
        }
        return value;
    }
}

// ── File-local types ──────────────────────────────────────────────────────────

/// <summary>Minimal projection of the attraction_updated SNS message body.</summary>
internal sealed record AttractionUpdateDto(
    string EntityId,
    string Name,
    string? Status,
    DateTimeOffset? LightningLaneTime,
    string? LightningLaneStatus,
    bool IsLightningLane,
    int? StandbyWaitTime,
    DateTimeOffset LastUpdated);


