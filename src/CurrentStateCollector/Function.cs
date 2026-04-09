using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SNSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using System.Text.Json;
using System.Text.Json.Serialization;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CurrentStateCollector;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _currentStateTable;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _updatesTopicArn;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parameterless constructor used by the Lambda runtime.
    /// Initialises AWS SDK clients once per container warm start so they are
    /// reused across invocations.
    /// </summary>
    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _sns      = new AmazonSimpleNotificationServiceClient();

        var tableName = Environment.GetEnvironmentVariable("CURRENT_STATE_TABLE");
        if (tableName is null)
        {
            Console.Error.WriteLine("[Function] Missing required environment variable: CURRENT_STATE_TABLE");
            throw new InvalidOperationException("CURRENT_STATE_TABLE environment variable is not set.");
        }

        var topicArn = Environment.GetEnvironmentVariable("UPDATES_TOPIC_ARN");
        if (topicArn is null)
        {
            Console.Error.WriteLine("[Function] Missing required environment variable: UPDATES_TOPIC_ARN");
            throw new InvalidOperationException("UPDATES_TOPIC_ARN environment variable is not set.");
        }

        _currentStateTable = tableName;
        _updatesTopicArn   = topicArn;
    }

    /// <summary>Constructor that accepts pre-built clients (for unit testing).</summary>
    public Function(
        IAmazonDynamoDB dynamoDb,
        string currentStateTable,
        IAmazonSimpleNotificationService sns,
        string updatesTopicArn)
    {
        _dynamoDb          = dynamoDb;
        _currentStateTable = currentStateTable;
        _sns               = sns;
        _updatesTopicArn   = updatesTopicArn;
    }

    /// <summary>
    /// Lambda handler – processes SNS notifications of entity changes and
    /// persists the latest entity state to the CurrentStateTable.
    /// </summary>
    /// <param name="snsEvent">The SNS event containing one or more entity-change records.</param>
    /// <param name="context">Lambda execution context (used for structured logging).</param>
    public async Task FunctionHandler(SNSEvent snsEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"Processing {snsEvent.Records.Count} SNS record(s).");

        foreach (var record in snsEvent.Records)
        {
            await PersistEntityAsync(record, context);
        }
    }

    /// <summary>
    /// Deserializes a single SNS record's message body as <see cref="EntityLiveData"/>
    /// and writes the current entity state to DynamoDB.
    /// </summary>
    private async Task PersistEntityAsync(SNSEvent.SNSRecord record, ILambdaContext context)
    {
        EntityLiveData entity;
        try
        {
            entity = JsonSerializer.Deserialize<EntityLiveData>(record.Sns.Message, JsonOptions)
                ?? throw new InvalidOperationException("Deserialized entity was null.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to deserialize SNS message into EntityLiveData: {ex.Message}. " +
                $"Raw message: {record.Sns.Message}");
            return;
        }

        var status          = entity.Status ?? "UNKNOWN";
        var isLightningLane = (entity.Queue?.HasLightningLane ?? false).ToString().ToLowerInvariant();

        try
        {
            await _dynamoDb.PutItemAsync(_currentStateTable, new Dictionary<string, AttributeValue>
            {
                ["EntityId"]        = new AttributeValue { S = entity.Id },
                ["Status"]          = new AttributeValue { S = status },
                ["IsLightningLane"] = new AttributeValue { S = isLightningLane },
                ["FullData"]        = new AttributeValue { S = record.Sns.Message },
            });

            context.Logger.LogInformation(
                $"Persisted entity '{entity.Id}' to CurrentStateTable " +
                $"(Status: {status}, IsLightningLane: {isLightningLane}).");

            // Build and serialize the DTO here (entity is file-local; method signatures
            // may not reference file-local types, so we pass the resolved string instead).
            var dto = new AttractionUpdateDto(
                EntityId:            entity.Id,
                Name:                entity.Name,
                Status:              entity.Status,
                LightningLaneTime:   entity.Queue?.ReturnTime?.ReturnStart
                                  ?? entity.Queue?.PaidReturnTime?.ReturnStart,
                LightningLaneStatus: entity.Queue?.ReturnTime?.State
                                  ?? entity.Queue?.PaidReturnTime?.State,
                IsLightningLane:     isLightningLane == "true",
                StandbyWaitTime:     entity.Queue?.Standby?.WaitTime,
                LastUpdated:         entity.LastUpdated);

            if (isLightningLane == "true")
                await PublishAttractionUpdateAsync(
                    JsonSerializer.Serialize(dto, JsonOptions), entity.Id, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to persist entity '{entity.Id}' to DynamoDB table '{_currentStateTable}': {ex.Message}");
        }
    }

    /// <summary>
    /// Publishes an <c>attraction_updated</c> notification to the UpdatesTopic after a
    /// successful DynamoDB write. Failures are logged and swallowed so they never
    /// prevent subsequent records in the batch from being processed.
    /// </summary>
    /// <param name="messageBody">Pre-serialized <c>AttractionUpdateDto</c> JSON.</param>
    /// <param name="entityId">Entity ID used in log messages.</param>
    private async Task PublishAttractionUpdateAsync(
        string messageBody, string entityId, ILambdaContext context)
    {
        try
        {
            var response = await _sns.PublishAsync(new PublishRequest
            {
                TopicArn = _updatesTopicArn,
                Message  = messageBody,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["event_type"] = new MessageAttributeValue
                    {
                        DataType    = "String",
                        StringValue = "attraction_updated",
                    },
                },
            });

            context.Logger.LogInformation(
                $"Published attraction_updated for '{entityId}'. MessageId: {response.MessageId}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to publish SNS notification for '{entityId}': {ex.Message}");
        }
    }
}

/// <summary>Minimal projection of the EntityLiveData payload published by ThemeParkPoller.</summary>
file sealed class EntityLiveData
{
    public string Id              { get; set; } = string.Empty;
    public string Name            { get; set; } = string.Empty;
    public string? Status         { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public LiveQueue? Queue       { get; set; }
}

/// <summary>
/// Minimal projection of LiveQueue. Property names match the upstream JSON keys exactly.
/// </summary>
file sealed class LiveQueue
{
    [JsonPropertyName("RETURN_TIME")]
    public ReturnTimeQueue? ReturnTime     { get; set; }

    [JsonPropertyName("PAID_RETURN_TIME")]
    public ReturnTimeQueue? PaidReturnTime { get; set; }

    [JsonPropertyName("STANDBY")]
    public StandbyQueue? Standby           { get; set; }

    /// <summary>
    /// True when either a Lightning Lane return-time slot exists for this entity.
    /// Mirrors the logic in <c>ThemeParkPoller.ApiClient.Models.LiveQueue.HasLightningLane</c>.
    /// </summary>
    public bool HasLightningLane => ReturnTime is not null || PaidReturnTime is not null;
}

/// <summary>Carries the return-time window and availability state for a Lightning Lane slot.</summary>
file sealed class ReturnTimeQueue
{
    public DateTimeOffset? ReturnStart { get; set; }
    public string? State              { get; set; }
}

/// <summary>Carries the current standby queue wait time in minutes.</summary>
file sealed class StandbyQueue
{
    public int? WaitTime { get; set; }
}

/// <summary>
/// DTO serialized as the SNS message body for <c>attraction_updated</c> events.
/// Schema mirrors <c>AttractionDto</c> in ThemeParkApi so downstream consumers
/// can use the same deserialization logic.
/// </summary>
file sealed record AttractionUpdateDto(
    string EntityId,
    string Name,
    string? Status,
    DateTimeOffset? LightningLaneTime,
    string? LightningLaneStatus,
    bool IsLightningLane,
    int? StandbyWaitTime,
    DateTimeOffset LastUpdated);

