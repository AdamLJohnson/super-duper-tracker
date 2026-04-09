using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AwsApigatewayv2Integrations;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Infra
{
    /// <summary>Props that wire the WebSocketStack to resources owned by the PollerStack.</summary>
    public class WebSocketStackProps : StackProps
    {
        /// <summary>
        /// The shared SNS topic from <see cref="PollerStack"/>.
        /// The broadcaster Lambda subscribes with <c>event_type = attraction_updated</c>.
        /// </summary>
        public ITopic UpdatesTopic { get; init; } = null!;
    }

    /// <summary>
    /// Provisions the real-time WebSocket infrastructure:
    /// <list type="bullet">
    ///   <item><b>ConnectionsTable</b> – DynamoDB table keyed on <c>ConnectionId</c>; tracks every active subscriber.</item>
    ///   <item><b>ConnectionHandler Lambda</b> – stores / removes connection IDs on <c>$connect</c> / <c>$disconnect</c>.</item>
    ///   <item><b>BroadcastHandler Lambda</b> – fans out <c>attraction_updated</c> SNS events to all live connections via the API Gateway Management API.</item>
    ///   <item><b>WebSocket API + Stage</b> – API Gateway WebSocket endpoint that clients connect to over <c>wss://</c>.</item>
    /// </list>
    /// </summary>
    public class WebSocketStack : Stack
    {
        internal WebSocketStack(Construct scope, string id, WebSocketStackProps props) : base(scope, id, props)
        {
            // ── DynamoDB table (active WebSocket connection IDs) ──────────────────
            var connectionsTable = new Table(this, "ConnectionsTable", new TableProps
            {
                PartitionKey  = new Attribute { Name = "ConnectionId", Type = AttributeType.STRING },
                BillingMode   = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = RemovalPolicy.DESTROY,
            });

            // ── Shared bundling options (both Lambdas use the same code asset) ────
            var bundling = new BundlingOptions
            {
                Image   = DockerImage.FromRegistry("public.ecr.aws/sam/build-dotnet10"),
                Command =
                [
                    "bash", "-c",
                    "dotnet publish -c Release -r linux-x64 --self-contained true -o /asset-output"
                ],
            };

            var codeAsset = Code.FromAsset("../src/WebSocketBroadcaster",
                new Amazon.CDK.AWS.S3.Assets.AssetOptions
                {
                    // SOURCE hashing fingerprints the local .cs/.csproj files before Docker runs,
                    // so `cdk synth` skips the Docker build entirely when nothing has changed.
                    // Both WsConnectionFunction and WsBroadcasterFunction share this asset object,
                    // so the hash is computed and Docker is invoked at most once per synth.
                    AssetHashType = Amazon.CDK.AssetHashType.SOURCE,
                    Exclude       = ["bin", "obj", ".vs", "*.user", "*.DotSettings.user"],
                    Bundling      = bundling,
                });

            // ── Lambda: connection lifecycle ($connect / $disconnect) ─────────────
            // WEBSOCKET_ENDPOINT is intentionally omitted here; ConnectionHandler never
            // calls the Management API, so the env var is not required for this function.
            var connectionFn = new Function(this, "WsConnectionFunction", new FunctionProps
            {
                Description  = "Stores and removes WebSocket connection IDs in ConnectionsTable on $connect / $disconnect events.",
                Runtime      = Runtime.DOTNET_10,
                Architecture = Architecture.X86_64,
                Handler      = "WebSocketBroadcaster::WebSocketBroadcaster.Function::ConnectionHandler",
                Code         = codeAsset,
                MemorySize   = 256,
                Timeout      = Duration.Seconds(10),
                SnapStart   = SnapStartConf.ON_PUBLISHED_VERSIONS,
                Environment  = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["CONNECTIONS_TABLE"] = connectionsTable.TableName,
                },
            });

            connectionsTable.GrantReadWriteData(connectionFn);

            // ── Lambda: SNS broadcaster ───────────────────────────────────────────
            // WEBSOCKET_ENDPOINT is added via AddEnvironment after the stage is created
            // so the resolved callback URL token is available.
            var broadcasterFn = new Function(this, "WsBroadcasterFunction", new FunctionProps
            {
                Description  = "Fans out SNS attraction_updated events to all active WebSocket subscribers via the API Gateway Management API.",
                Runtime      = Runtime.DOTNET_10,
                Architecture = Architecture.X86_64,
                Handler      = "WebSocketBroadcaster::WebSocketBroadcaster.Function::BroadcastHandler",
                Code         = codeAsset,
                MemorySize   = 256,
                Timeout      = Duration.Seconds(30),
                SnapStart   = SnapStartConf.ON_PUBLISHED_VERSIONS,
                Environment  = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["CONNECTIONS_TABLE"] = connectionsTable.TableName,
                },
            });

            connectionsTable.GrantReadWriteData(broadcasterFn);

            var connectionFnVersion = connectionFn.CurrentVersion;

            var connectionFnAlias = new Alias(this, "WsConnectionFunctionLiveAlias", new AliasProps
            {
                AliasName = "live",
                Version   = connectionFnVersion,
            });

            // ── WebSocket API ─────────────────────────────────────────────────────
            var wsApi = new WebSocketApi(this, "ThemeParkWsApi", new WebSocketApiProps
            {
                ApiName     = "ThemeParkWsApi",
                Description = "WebSocket API for real-time Disneyland Resort attraction updates.",
                ConnectRouteOptions = new WebSocketRouteOptions
                {
                    Integration = new WebSocketLambdaIntegration("ConnectIntegration", connectionFnAlias),
                },
                DisconnectRouteOptions = new WebSocketRouteOptions
                {
                    Integration = new WebSocketLambdaIntegration("DisconnectIntegration", connectionFnAlias),
                },
            });

            // ── WebSocket stage ───────────────────────────────────────────────────
            var wsStage = new WebSocketStage(this, "ThemeParkWsStage", new WebSocketStageProps
            {
                WebSocketApi = wsApi,
                StageName    = "prod",
                AutoDeploy   = true,
            });

            // ── Inject WEBSOCKET_ENDPOINT now that the stage callback URL is known ─
            // wsStage.CallbackUrl → "https://{api-id}.execute-api.{region}.amazonaws.com/prod"
            // Only the broadcaster needs this; ConnectionHandler never calls PostToConnection.
            broadcasterFn.AddEnvironment("WEBSOCKET_ENDPOINT", wsStage.CallbackUrl);

            // Grant the broadcaster permission to call PostToConnection on this API.
            wsApi.GrantManageConnections(broadcasterFn);

            var broadcasterFnVersion = broadcasterFn.CurrentVersion;

            var broadcasterFnAlias = new Alias(this, "ThemeParkWsApiLiveAlias", new AliasProps
            {
                AliasName = "live",
                Version   = broadcasterFnVersion,
            });

            // ── SNS subscription (real-time client events) ────────────────────────
            // attraction_updated: live wait-time / status patches for attraction cards.
            // sparkline_updated:  refreshed 24-point trend series for sparkline overlays.
            props.UpdatesTopic.AddSubscription(new LambdaSubscription(broadcasterFnAlias, new LambdaSubscriptionProps
            {
                FilterPolicy = new System.Collections.Generic.Dictionary<string, SubscriptionFilter>
                {
                    ["event_type"] = SubscriptionFilter.StringFilter(new StringConditions
                    {
                        Allowlist = ["attraction_updated", "sparkline_updated"],
                    }),
                },
            }));

            // ── Outputs ───────────────────────────────────────────────────────────
            new CfnOutput(this, "WebSocketUrl", new CfnOutputProps
            {
                Value       = wsStage.Url,
                Description = "WSS endpoint clients use to subscribe to real-time attraction updates.",
            });

            new CfnOutput(this, "WebSocketCallbackUrl", new CfnOutputProps
            {
                Value       = wsStage.CallbackUrl,
                Description = "HTTPS Management API endpoint used by the broadcaster Lambda.",
            });
        }
    }
}

