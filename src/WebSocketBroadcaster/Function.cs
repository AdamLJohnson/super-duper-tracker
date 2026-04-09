using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.SNSEvents;
using System.Text;
using System.Text.Json;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace WebSocketBroadcaster;

/// <summary>
/// Hosts two Lambda handlers that together implement the WebSocket real-time broadcast pipeline:
/// <list type="bullet">
///   <item><term>ConnectionHandler</term><description>Stores or removes a WebSocket connection ID in DynamoDB on $connect / $disconnect events from API Gateway.</description></item>
///   <item><term>BroadcastHandler</term><description>Receives SNS <c>attraction_updated</c> notifications and fans them out to every active WebSocket subscriber via the API Gateway Management API.</description></item>
/// </list>
/// Both handlers live in a single code asset so only one Docker build is required per CDK deployment.
/// </summary>
public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _connectionsTable;
    private readonly string _webSocketEndpoint;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parameterless constructor used by the Lambda runtime.
    /// <para>
    /// <c>WEBSOCKET_ENDPOINT</c> is optional here so that the <c>WsConnectionFunction</c>
    /// Lambda (which only handles <c>$connect</c> / <c>$disconnect</c>) does not need the
    /// Management API URL configured. The broadcaster function sets it and the
    /// <c>BroadcastHandler</c> will surface a clear error if it is absent.
    /// </para>
    /// </summary>
    public Function()
    {
        _dynamoDb          = new AmazonDynamoDBClient();
        _connectionsTable  = GetRequiredEnv("CONNECTIONS_TABLE");
        _webSocketEndpoint = Environment.GetEnvironmentVariable("WEBSOCKET_ENDPOINT") ?? string.Empty;
    }

    /// <summary>Constructor that accepts pre-built clients (for unit testing).</summary>
    public Function(IAmazonDynamoDB dynamoDb, string connectionsTable, string webSocketEndpoint)
    {
        _dynamoDb          = dynamoDb;
        _connectionsTable  = connectionsTable;
        _webSocketEndpoint = webSocketEndpoint;
    }

    // ── Handler: $connect / $disconnect ──────────────────────────────────────

    /// <summary>
    /// Invoked by API Gateway for both <c>$connect</c> and <c>$disconnect</c> route keys.
    /// Adds or removes the connection ID from the <c>ConnectionsTable</c> so the broadcaster
    /// always has an up-to-date subscriber list.
    /// </summary>
    public async Task<APIGatewayProxyResponse> ConnectionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var connectionId = request.RequestContext.ConnectionId;
        var routeKey     = request.RequestContext.RouteKey;

        try
        {
            if (routeKey == "$connect")
            {
                await _dynamoDb.PutItemAsync(_connectionsTable, new Dictionary<string, AttributeValue>
                {
                    ["ConnectionId"] = new AttributeValue { S = connectionId },
                });
                context.Logger.LogInformation($"[Connect]    Stored connection '{connectionId}'.");
            }
            else // $disconnect
            {
                await _dynamoDb.DeleteItemAsync(_connectionsTable, new Dictionary<string, AttributeValue>
                {
                    ["ConnectionId"] = new AttributeValue { S = connectionId },
                });
                context.Logger.LogInformation($"[Disconnect] Removed connection '{connectionId}'.");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"[ConnectionHandler] Failed to process '{routeKey}' for '{connectionId}': {ex.Message}");
            return new APIGatewayProxyResponse { StatusCode = 500 };
        }

        return new APIGatewayProxyResponse { StatusCode = 200 };
    }

    // ── Handler: SNS attraction_updated → WebSocket broadcast ─────────────────

    /// <summary>
    /// Invoked by SNS for every <c>attraction_updated</c> message published by
    /// <c>CurrentStateCollector</c>. Scans all active connection IDs and posts
    /// the message to each subscriber. Connections that return HTTP 410 Gone are
    /// automatically pruned from the table.
    /// </summary>
    public async Task BroadcastHandler(SNSEvent snsEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"[Broadcast] Processing {snsEvent.Records.Count} SNS record(s).");

        var connections = await ScanConnectionsAsync(context);
        if (connections.Count == 0)
        {
            context.Logger.LogInformation("[Broadcast] No active connections – nothing to broadcast.");
            return;
        }

        foreach (var record in snsEvent.Records)
        {
            //get the event type from the SNS message attributes
            if (!record.Sns.MessageAttributes.TryGetValue("event_type", out var attribute))
                continue;
            var eventType = attribute.Value;
            var payload = BuildWsPayload(eventType, record.Sns.Message);
            await PostToConnectionsAsync(payload, connections, context);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Pages through the entire <c>ConnectionsTable</c> and returns all connection IDs.
    /// </summary>
    private async Task<List<string>> ScanConnectionsAsync(ILambdaContext context)
    {
        var ids = new List<string>();
        ScanResponse? response = null;

        do
        {
            response = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName            = _connectionsTable,
                ProjectionExpression = "ConnectionId",
                ExclusiveStartKey    = response?.LastEvaluatedKey,
            });

            ids.AddRange(
                response.Items
                    .Where(i => i.ContainsKey("ConnectionId"))
                    .Select(i => i["ConnectionId"].S));
        }
        while (response.LastEvaluatedKey?.Count > 0);

        context.Logger.LogInformation($"[Broadcast] Found {ids.Count} active connection(s).");
        return ids;
    }

    /// <summary>
    /// Posts <paramref name="payload"/> to every connection in <paramref name="connectionIds"/>.
    /// 410 Gone responses indicate the client disconnected without triggering <c>$disconnect</c>;
    /// those stale entries are removed from the table.
    /// </summary>
    private async Task PostToConnectionsAsync(
        ReadOnlyMemory<byte> payload,
        List<string> connectionIds,
        ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_webSocketEndpoint))
        {
            context.Logger.LogError(
                "[Broadcast] WEBSOCKET_ENDPOINT environment variable is not set. Cannot post to connections.");
            return;
        }

        using var managementClient = new AmazonApiGatewayManagementApiClient(
            new AmazonApiGatewayManagementApiConfig { ServiceURL = _webSocketEndpoint });

        var stale = new List<string>();

        foreach (var id in connectionIds)
        {
            try
            {
                await managementClient.PostToConnectionAsync(new PostToConnectionRequest
                {
                    ConnectionId = id,
                    Data         = new MemoryStream(payload.ToArray()),
                });
                context.Logger.LogInformation($"[Broadcast] Posted to connection '{id}'.");
            }
            catch (GoneException)
            {
                context.Logger.LogInformation($"[Broadcast] Stale connection '{id}' – scheduling removal.");
                stale.Add(id);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"[Broadcast] Failed to post to '{id}': {ex.Message}");
            }
        }

        foreach (var id in stale)
            await RemoveStaleConnectionAsync(id, context);
    }

    /// <summary>Removes a stale connection ID from the <c>ConnectionsTable</c>.</summary>
    private async Task RemoveStaleConnectionAsync(string connectionId, ILambdaContext context)
    {
        try
        {
            await _dynamoDb.DeleteItemAsync(_connectionsTable, new Dictionary<string, AttributeValue>
            {
                ["ConnectionId"] = new AttributeValue { S = connectionId },
            });
            context.Logger.LogInformation($"[Broadcast] Removed stale connection '{connectionId}'.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"[Broadcast] Failed to remove stale connection '{connectionId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Serializes an SNS message body into the WebSocket envelope format:
    /// <code>
    /// {
    ///   "event_type": "attraction_updated",
    ///   "event_data": { "EntityId": "...", "Name": "...", ... }
    /// }
    /// </code>
    /// <para>
    /// The <c>event_type</c> field is the SNS <c>event_type</c> message attribute forwarded
    /// verbatim, so any future event type published to the topic is broadcast without
    /// requiring changes to this Lambda.
    /// </para>
    /// <para>
    /// The original SNS JSON is parsed as a <see cref="JsonDocument"/> and its properties
    /// are written directly into the <c>event_data</c> object via <see cref="Utf8JsonWriter"/>,
    /// avoiding a full round-trip through a DTO.
    /// </para>
    /// </summary>
    private static ReadOnlyMemory<byte> BuildWsPayload(string eventType, string snsMessageJson)
    {
        using var doc    = JsonDocument.Parse(snsMessageJson);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        writer.WriteString("event_type", eventType);

        writer.WritePropertyName("event_data");
        writer.WriteStartObject();
        foreach (var property in doc.RootElement.EnumerateObject())
            property.WriteTo(writer);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
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

