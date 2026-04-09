using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Refit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThemeParkPoller.ApiClient;
using ThemeParkPoller.ApiClient.Models;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ThemeParkPoller;

public class Function
{
    private const string DestinationSlug = "disneylandresort";
    private const string BaseUrl = "https://api.themeparks.wiki/v1";

    private readonly IThemeParksApi _api;
    private readonly IAmazonDynamoDB? _dynamoDb;
    private readonly IAmazonSimpleNotificationService? _sns;
    private readonly string? _stateTableName;
    private readonly string? _updatesTopicArn;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parameterless constructor used by the Lambda runtime.
    /// Initialises the Refit client and AWS SDK clients once per container warm start
    /// so they are reused across invocations.
    /// </summary>
    public Function()
    {
        _api = RestService.For<IThemeParksApi>(BaseUrl, new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(JsonOptions),
        });

        _dynamoDb = new AmazonDynamoDBClient();
        _sns = new AmazonSimpleNotificationServiceClient();

        _stateTableName = Environment.GetEnvironmentVariable("STATE_TABLE_NAME");
        if (_stateTableName is null)
        {
            Console.Error.WriteLine("[Function] Missing required environment variable: STATE_TABLE_NAME");
            throw new InvalidOperationException("STATE_TABLE_NAME environment variable is not set.");
        }

        _updatesTopicArn = Environment.GetEnvironmentVariable("UPDATES_TOPIC_ARN");
        if (_updatesTopicArn is null)
        {
            Console.Error.WriteLine("[Function] Missing required environment variable: UPDATES_TOPIC_ARN");
            throw new InvalidOperationException("UPDATES_TOPIC_ARN environment variable is not set.");
        }
    }

    /// <summary>Constructor that accepts pre-built clients (for unit testing).</summary>
    public Function(
        IThemeParksApi api,
        IAmazonDynamoDB dynamoDb,
        IAmazonSimpleNotificationService sns,
        string stateTableName,
        string updatesTopicArn)
    {
        _api = api;
        _dynamoDb = dynamoDb;
        _sns = sns;
        _stateTableName = stateTableName;
        _updatesTopicArn = updatesTopicArn;
    }

    /// <summary>Constructor that accepts only an API client (for lightweight unit testing).</summary>
    public Function(IThemeParksApi api) => _api = api;

    /// <summary>
    /// Lambda handler – fetches live wait-time and status data for the
    /// Disneyland Resort destination, detects changes via DynamoDB hashes,
    /// and publishes change notifications to SNS.
    /// </summary>
    /// <param name="context">Lambda execution context (used for structured logging).</param>
    public async Task FunctionHandler(ILambdaContext context)
    {
        context.Logger.LogInformation($"Fetching live data for destination: {DestinationSlug}");

        EntityLiveDataResponse liveData;
        try
        {
            liveData = await _api.GetEntityLiveDataAsync(DestinationSlug);
        }
        catch (ApiException ex)
        {
            context.Logger.LogError(
                $"API request failed for destination '{DestinationSlug}': " +
                $"HTTP {(int)ex.StatusCode} {ex.StatusCode} – {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Unexpected error fetching live data for destination '{DestinationSlug}': {ex.Message}");
            return;
        }

        context.Logger.LogInformation(
            $"Retrieved {liveData.LiveData.Count} live-data entries for '{liveData.Name}'.");

        if (_dynamoDb is not null && _sns is not null &&
            _stateTableName is not null && _updatesTopicArn is not null)
        {
            await DetectAndPublishChangesAsync(liveData.LiveData, context);
        }
    }

    /// <summary>
    /// For each entity, computes a hash of its current state, compares it against
    /// the stored hash in DynamoDB, and publishes an SNS notification when changed.
    /// All existing hashes are loaded in a single DynamoDB Scan before the loop to
    /// avoid one round-trip per entity.
    /// </summary>
    private async Task DetectAndPublishChangesAsync(
        IEnumerable<EntityLiveData> entities,
        ILambdaContext context)
    {
        var entityList = entities.ToList();
        int changesPublished = 0;

        context.Logger.LogInformation(
            $"Starting change detection for {entityList.Count} entities (destination: {DestinationSlug}).");

        // ── Bulk load all stored hashes from DynamoDB ─────────────────────────
        Dictionary<string, string> storedHashes;
        try
        {
            var scanResponse = await _dynamoDb!.ScanAsync(new ScanRequest
            {
                TableName = _stateTableName,
                ProjectionExpression = "EntityId, StateHash",
            });

            storedHashes = scanResponse.Items
                .Where(item => item.TryGetValue("EntityId",  out var id) && id.S is not null
                            && item.TryGetValue("StateHash", out var h)  && h.S  is not null)
                .ToDictionary(
                    item => item["EntityId"].S,
                    item => item["StateHash"].S);

            context.Logger.LogInformation(
                $"Loaded {storedHashes.Count} stored state record(s) from DynamoDB.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to scan DynamoDB state table '{_stateTableName}'. " +
                $"Aborting change detection to prevent redundant notifications: {ex.Message}");
            return;
        }

        // ── Process each entity against the in-memory hash map ────────────────
        foreach (var entity in entityList)
        {
            try
            {
                var currentHash = ComputeHash(entity);

                storedHashes.TryGetValue(entity.Id, out var storedHash);

                if (currentHash == storedHash)
                    continue;

                context.Logger.LogInformation(
                    $"Change detected for entity '{entity.Id}' ({entity.Name}). Updating state and publishing notification.");

                // Persist the updated hash to DynamoDB
                await _dynamoDb!.PutItemAsync(_stateTableName, new Dictionary<string, AttributeValue>
                {
                    ["EntityId"] = new AttributeValue { S = entity.Id },
                    ["StateHash"] = new AttributeValue { S = currentHash },
                });

                // Publish change notification to SNS
                var publishResponse = await _sns!.PublishAsync(new PublishRequest
                {
                    TopicArn = _updatesTopicArn,
                    Message  = JsonSerializer.Serialize(entity, JsonOptions),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["event_type"] = new MessageAttributeValue
                        {
                            DataType    = "String",
                            StringValue = "api_update",
                        },
                    },
                });

                changesPublished++;
                context.Logger.LogInformation(
                    $"SNS notification published for entity '{entity.Id}' ({entity.Name}). MessageId: {publishResponse.MessageId}");
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Failed to process entity '{entity.Id}' ({entity.Name}): {ex.Message}");
            }
        }

        context.Logger.LogInformation(
            $"Change detection complete. Checked: {entityList.Count} entities, Changes published: {changesPublished}.");
    }

    /// <summary>Computes a SHA-256 hash of the serialized entity state.</summary>
    private static string ComputeHash(EntityLiveData entity)
    {
        var json = JsonSerializer.Serialize(entity, JsonOptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes);
    }
}
