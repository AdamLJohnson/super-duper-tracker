using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Scheduler;
using Amazon.CDK.AWS.Scheduler.Targets;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Infra
{
    public class PollerStack : Stack
    {
        /// <summary>The CurrentStateTable, shared with the ApiStack for read access.</summary>
        public ITable CurrentStateTable { get; }

        /// <summary>
        /// The shared SNS topic that carries all entity-change events.
        /// <list type="bullet">
        ///   <item><c>event_type = api_update</c> – raw poller output consumed by <c>CurrentStateCollector</c>.</item>
        ///   <item><c>event_type = attraction_updated</c> – processed DTO published by <c>CurrentStateCollector</c>, consumed by <c>WebSocketBroadcaster</c>.</item>
        /// </list>
        /// </summary>
        public ITopic UpdatesTopic { get; }

        internal PollerStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // ── DynamoDB table (entity state hashes) ──────────────────────────────
            var entityStateTable = new Table(this, "EntityStateTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "EntityId", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = RemovalPolicy.DESTROY,
            });

            // ── DynamoDB table (current live state per entity) ────────────────────
            var currentStateTable = new Table(this, "CurrentStateTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "EntityId", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = RemovalPolicy.DESTROY,
            });
            CurrentStateTable = currentStateTable; // expose as ITable for cross-stack reference

            currentStateTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
            {
                IndexName    = "StatusIndex",
                PartitionKey = new Attribute { Name = "Status", Type = AttributeType.STRING },
            });

            currentStateTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
            {
                IndexName    = "LightningLaneIndex",
                PartitionKey = new Attribute { Name = "IsLightningLane", Type = AttributeType.STRING },
            });

            // ── SNS topic (entity change notifications) ───────────────────────────
            var updatesTopic = new Topic(this, "UpdatesTopic", new TopicProps
            {
                DisplayName = "ThemePark Entity Updates",
            });
            UpdatesTopic = updatesTopic; // expose as ITopic for cross-stack subscriptions

            // ── Lambda function ───────────────────────────────────────────────────
            var pollerFn = new Function(this, "ThemeParkPollerFunction", new FunctionProps
            {
                Description = "Polls live wait-time and status data for Disneyland Resort via the ThemeParks API.",
                Runtime = Runtime.DOTNET_10,
                Architecture = Architecture.X86_64,
                Handler = "ThemeParkPoller::ThemeParkPoller.Function::FunctionHandler",
                Code = Code.FromAsset("../src/ThemeParkPoller", new Amazon.CDK.AWS.S3.Assets.AssetOptions
                {
                    // SOURCE hashing fingerprints the local .cs/.csproj files before Docker runs,
                    // so `cdk synth` skips the Docker build entirely when nothing has changed.
                    AssetHashType = Amazon.CDK.AssetHashType.SOURCE,
                    Exclude       = ["bin", "obj", ".vs", "*.user", "*.DotSettings.user"],
                    Bundling = new BundlingOptions
                    {
                        // public.ecr.aws/sam/build-dotnet10 provides the .NET 10 SDK on AL2023.
                        Image = DockerImage.FromRegistry("public.ecr.aws/sam/build-dotnet10"),
                        Command =
                        [
                            "bash", "-c",
                            "dotnet publish -c Release -r linux-x64 --self-contained true -o /asset-output"
                        ],
                    },
                }),
                MemorySize = 256,
                Timeout = Duration.Seconds(30),
                //SnapStart   = SnapStartConf.ON_PUBLISHED_VERSIONS,
                Environment = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["STATE_TABLE_NAME"] = entityStateTable.TableName,
                    ["UPDATES_TOPIC_ARN"] = updatesTopic.TopicArn,
                },
            });

            // ── Permissions (poller) ──────────────────────────────────────────────
            entityStateTable.GrantReadWriteData(pollerFn);
            updatesTopic.GrantPublish(pollerFn);

            // ── CurrentStateCollector Lambda ──────────────────────────────────────
            var collectorFn = new Function(this, "CurrentStateCollectorFunction", new FunctionProps
            {
                Description = "Consumes entity-change notifications from SNS and persists the latest live state to CurrentStateTable.",
                Runtime     = Runtime.DOTNET_10,
                Architecture = Architecture.X86_64,
                Handler = "CurrentStateCollector::CurrentStateCollector.Function::FunctionHandler",
                Code = Code.FromAsset("../src/CurrentStateCollector", new Amazon.CDK.AWS.S3.Assets.AssetOptions
                {
                    // SOURCE hashing fingerprints the local .cs/.csproj files before Docker runs,
                    // so `cdk synth` skips the Docker build entirely when nothing has changed.
                    AssetHashType = Amazon.CDK.AssetHashType.SOURCE,
                    Exclude       = ["bin", "obj", ".vs", "*.user", "*.DotSettings.user"],
                    Bundling = new BundlingOptions
                    {
                        Image = DockerImage.FromRegistry("public.ecr.aws/sam/build-dotnet10"),
                        Command =
                        [
                            "bash", "-c",
                            "dotnet publish -c Release -r linux-x64 --self-contained true -o /asset-output"
                        ],
                    },
                }),
                MemorySize = 256,
                Timeout    = Duration.Seconds(30),
                //SnapStart   = SnapStartConf.ON_PUBLISHED_VERSIONS,
                Environment = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["CURRENT_STATE_TABLE"] = currentStateTable.TableName,
                    ["UPDATES_TOPIC_ARN"]   = updatesTopic.TopicArn,
                },
            });

            // ── Permissions (collector) ───────────────────────────────────────────
            currentStateTable.GrantWriteData(collectorFn);
            updatesTopic.GrantPublish(collectorFn);

            var collectorFnVersion = collectorFn.CurrentVersion;

            var collectorFnAlias = new Alias(this, "CurrentStateCollectorFunctionLiveAlias", new AliasProps
            {
                AliasName = "live",
                Version   = collectorFnVersion,
            });

            // ── SNS subscription with event_type filter ───────────────────────────
            updatesTopic.AddSubscription(new LambdaSubscription(collectorFnAlias, new LambdaSubscriptionProps
            {
                FilterPolicy = new System.Collections.Generic.Dictionary<string, SubscriptionFilter>
                {
                    ["event_type"] = SubscriptionFilter.StringFilter(new StringConditions
                    {
                        Allowlist = ["api_update"],
                    }),
                },
            }));

            var pollerFnVersion = pollerFn.CurrentVersion;

            var pollerFnAlias = new Alias(this, "ThemeParkPollerFunctionLiveAlias", new AliasProps
            {
                AliasName = "live",
                Version   = pollerFnVersion,
            });

            // ── EventBridge schedule ──────────────────────────────────────────────
            var pollerSchedule = new Amazon.CDK.AWS.Scheduler.Schedule(this, "ThemeParkPollerSchedule", new ScheduleProps
            {
                Description = "Triggers ThemeParkPoller every 5 minutes to poll disneylandresort live data.",
                Schedule = ScheduleExpression.Rate(Duration.Minutes(1)),
                Target = new LambdaInvoke(pollerFnAlias),
            });
        }
    }
}
