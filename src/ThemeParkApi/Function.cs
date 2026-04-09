using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using SparklineShared;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ThemeParkApi;

public class Function
{
    private const string LightningLaneIndex = "LightningLaneIndex";
    private const string StatusIndex        = "StatusIndex";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _stateTableName;
    private readonly string _sparklineTableName;

    /// <summary>
    /// Parameterless constructor used by the Lambda runtime.
    /// Initialises the DynamoDB client once per container warm start so it is
    /// reused across invocations.
    /// </summary>
    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _stateTableName    = GetRequiredEnv("STATE_TABLE_NAME");
        _sparklineTableName = GetRequiredEnv("SPARKLINE_TABLE_NAME");
    }

    /// <summary>Constructor that accepts pre-built clients (for unit testing).</summary>
    public Function(IAmazonDynamoDB dynamoDb, string stateTableName, string sparklineTableName)
    {
        _dynamoDb           = dynamoDb;
        _stateTableName     = stateTableName;
        _sparklineTableName = sparklineTableName;
    }

    /// <summary>
    /// Lambda handler – routes API Gateway requests to the appropriate DynamoDB GSI query.
    /// </summary>
    /// <param name="request">The API Gateway proxy request.</param>
    /// <param name="context">Lambda execution context (used for structured logging).</param>
    /// <returns>An API Gateway proxy response containing a JSON array of attractions.</returns>
    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        context.Logger.LogInformation($"Received GET {request.Resource}");

        switch (request.Resource)
        {
            case "/attractions/lightning-lane":
                context.Logger.LogInformation(
                    "Querying LightningLaneIndex for IsLightningLane = \"true\".");
                return await QueryGsiAsync(LightningLaneIndex, "IsLightningLane", "true", context);

            case "/attractions/status/{status}":
                if (!request.PathParameters.TryGetValue("status", out var status))
                    return BuildResponse(400, "{\"error\":\"Missing required path parameter: status.\"}");
                context.Logger.LogInformation($"Querying StatusIndex for Status = \"{status}\".");
                return await QueryGsiAsync(StatusIndex, "Status", status, context);

            case "/sparklines/lightning-lane":
                context.Logger.LogInformation(
                    "Querying SparklineStore LightningLaneIndex for IsLightningLane = \"true\".");
                return await QuerySparklineGsiAsync(context);

            default:
                return BuildResponse(404, $"{{\"error\":\"Route not found: {request.Resource}\"}}");
        }
    }

    /// <summary>
    /// Queries the specified GSI, deserializes each item's <c>FullData</c> attribute,
    /// and maps results to <see cref="AttractionDto"/>.
    /// </summary>
    private async Task<APIGatewayProxyResponse> QueryGsiAsync(
        string indexName, string keyName, string keyValue, ILambdaContext context)
    {
        QueryResponse queryResponse;
        try
        {
            queryResponse = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName                 = _stateTableName,
                IndexName                 = indexName,
                KeyConditionExpression    = "#key = :val",
                ExpressionAttributeNames  = new Dictionary<string, string> { ["#key"] = keyName },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":val"] = new AttributeValue { S = keyValue },
                },
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"DynamoDB query on {indexName} failed: {ex.Message}");
            return BuildResponse(500, "{\"error\":\"An error occurred querying the data store.\"}");
        }

        var results = new List<AttractionDto>();
        foreach (var item in queryResponse.Items)
        {
            if (!item.TryGetValue("FullData", out var fd) || fd.S is null)
            {
                context.Logger.LogError(
                    $"Item is missing FullData attribute: EntityId={item.GetValueOrDefault("EntityId")?.S}");
                continue;
            }

            EntityLiveData? entity;
            try
            {
                entity = JsonSerializer.Deserialize<EntityLiveData>(fd.S, SparklineMaterializer.JsonOptions);
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Failed to deserialize FullData for '{item.GetValueOrDefault("EntityId")?.S}': {ex.Message}");
                continue;
            }

            if (entity is null) continue;

            results.Add(new AttractionDto(
                EntityId:            item["EntityId"].S,
                Name:                entity.Name,
                Status:              entity.Status,
                LightningLaneTime:   entity.Queue?.ReturnTime?.ReturnStart
                                  ?? entity.Queue?.PaidReturnTime?.ReturnStart,
                LightningLaneStatus: entity.Queue?.ReturnTime?.State
                                  ?? entity.Queue?.PaidReturnTime?.State,
                IsLightningLane:     item["IsLightningLane"].S == "true",
                StandbyWaitTime:     entity.Queue?.Standby?.WaitTime,
                LastUpdated:         entity.LastUpdated));
        }

        context.Logger.LogInformation(
            $"Returning {results.Count} attraction(s) from {indexName}.");
        return BuildResponse(200, JsonSerializer.Serialize(results, SparklineMaterializer.JsonOptions));
    }

    /// <summary>
    /// Queries the <c>SparklineStore</c> GSI for all Lightning Lane sparklines,
    /// materializes each item's sparse <c>BucketsJson</c> into a fixed 24-slot array,
    /// and returns the collection as a JSON array of <see cref="SparklineDto"/>.
    /// </summary>
    private async Task<APIGatewayProxyResponse> QuerySparklineGsiAsync(ILambdaContext context)
    {
        QueryResponse queryResponse;
        try
        {
            queryResponse = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName                 = _sparklineTableName,
                IndexName                 = LightningLaneIndex,
                KeyConditionExpression    = "#key = :val",
                ExpressionAttributeNames  = new Dictionary<string, string> { ["#key"] = "IsLightningLane" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":val"] = new AttributeValue { S = "true" },
                },
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"SparklineStore GSI query failed: {ex.Message}");
            return BuildResponse(500, "{\"error\":\"An error occurred querying the sparkline store.\"}");
        }

        var results = new List<SparklineDto>();

        foreach (var item in queryResponse.Items)
        {
            if (!item.TryGetValue("EntityId", out var idAttr)) continue;

            var sparse = new Dictionary<DateTimeOffset, int>();

            if (item.TryGetValue("BucketsJson", out var bj) && bj.S is not null)
            {
                try
                {
                    sparse = SparklineMaterializer.ParseBucketsJson(bj.S);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(
                        $"Failed to parse BucketsJson for '{idAttr.S}': {ex.Message}");
                }
            }

            var buckets = SparklineMaterializer.Materialize(sparse, SparklineMaterializer.ParseSeedValue(item));

            results.Add(new SparklineDto(
                EntityId:        idAttr.S!,
                Name:            item.TryGetValue("Name", out var n) ? n.S ?? "" : "",
                IsLightningLane: true,
                Buckets:         buckets));
        }

        context.Logger.LogInformation($"Returning {results.Count} sparkline(s).");
        return BuildResponse(200, JsonSerializer.Serialize(results, SparklineMaterializer.JsonOptions));
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

    private static APIGatewayProxyResponse BuildResponse(int statusCode, string body) => new()
    {
        StatusCode = statusCode,
        Body       = body,
        Headers    = new Dictionary<string, string>
        {
            ["Content-Type"]                = "application/json",
            ["Access-Control-Allow-Origin"] = "*",
        },
    };
}

/// <summary>Minimal projection of the EntityLiveData JSON stored in the FullData attribute.</summary>
file sealed class EntityLiveData
{
    public string Id              { get; set; } = string.Empty;
    public string Name            { get; set; } = string.Empty;
    public string? Status         { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public LiveQueue? Queue       { get; set; }
}

/// <summary>
/// Minimal projection of LiveQueue carrying only the attributes needed to derive
/// Lightning Lane availability, return time, and standby wait time.
/// </summary>
file sealed class LiveQueue
{
    [JsonPropertyName("RETURN_TIME")]
    public ReturnTimeQueue? ReturnTime     { get; set; }

    [JsonPropertyName("PAID_RETURN_TIME")]
    public ReturnTimeQueue? PaidReturnTime { get; set; }

    [JsonPropertyName("STANDBY")]
    public StandbyQueue? Standby          { get; set; }
}

/// <summary>Carries the current standby queue wait time in minutes.</summary>
file sealed class StandbyQueue
{
    public int? WaitTime { get; set; }
}

/// <summary>Carries the return-time window and availability state for a Lightning Lane slot.</summary>
file sealed class ReturnTimeQueue
{
    public DateTimeOffset? ReturnStart { get; set; }
    public string? State              { get; set; }
}

/// <summary>Clean DTO returned in attraction API responses.</summary>
file sealed record AttractionDto(
    string EntityId,
    string Name,
    string? Status,
    DateTimeOffset? LightningLaneTime,
    string? LightningLaneStatus,
    bool IsLightningLane,
    int? StandbyWaitTime,
    DateTimeOffset LastUpdated);



