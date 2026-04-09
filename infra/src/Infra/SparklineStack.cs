using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Infra
{
    /// <summary>Props that wire the SparklineStack to resources owned by the PollerStack.</summary>
    public class SparklineStackProps : StackProps
    {
        /// <summary>
        /// The shared SNS topic from <see cref="PollerStack"/>.
        /// <see cref="SparklineStack"/> subscribes with <c>event_type = attraction_updated</c>
        /// and publishes back with <c>event_type = sparkline_updated</c>.
        /// </summary>
        public ITopic UpdatesTopic { get; init; } = null!;
    }

    /// <summary>
    /// Provisions the sparkline trend-tracking infrastructure:
    /// <list type="bullet">
    ///   <item><b>SparklineStore</b> – DynamoDB table keyed on <c>EntityId</c> with a
    ///     <c>LightningLaneIndex</c> GSI on <c>IsLightningLane</c> for cold-start bulk reads.</item>
    ///   <item><b>SparklineProcessor Lambda</b> – maintains 24 × 5-minute buckets per attraction
    ///     and publishes <c>sparkline_updated</c> events for real-time client delivery.</item>
    /// </list>
    /// </summary>
    public class SparklineStack : Stack
    {
        /// <summary>The SparklineStore table, shared with <see cref="ApiStack"/> for read access.</summary>
        public ITable SparklineTable { get; }

        internal SparklineStack(Construct scope, string id, SparklineStackProps props) : base(scope, id, props)
        {
            // ── DynamoDB table (sparkline series per attraction) ──────────────────
            var sparklineTable = new Table(this, "SparklineStore", new TableProps
            {
                PartitionKey = new Attribute { Name = "EntityId", Type = AttributeType.STRING },
                BillingMode  = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = RemovalPolicy.DESTROY,
            });

            // GSI: allows ThemeParkApi to retrieve all LL sparklines in one query.
            sparklineTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
            {
                IndexName    = "LightningLaneIndex",
                PartitionKey = new Attribute { Name = "IsLightningLane", Type = AttributeType.STRING },
            });

            SparklineTable = sparklineTable;

            // ── Lambda function (bucket maintenance + SNS publish) ────────────────
            var processorFn = new Function(this, "SparklineProcessorFunction", new FunctionProps
            {
                Description  = "Maintains 24-point / 5-minute sparkline buckets for LL attractions and publishes sparkline_updated events.",
                Runtime      = Runtime.DOTNET_10,
                Architecture = Architecture.X86_64,
                Handler      = "SparklineProcessor::SparklineProcessor.Function::FunctionHandler",
                Code = Code.FromAsset("../src", new Amazon.CDK.AWS.S3.Assets.AssetOptions
                {
                    // SOURCE hashing fingerprints the local .cs/.csproj files before Docker runs,
                    // so `cdk synth` skips the Docker build entirely when nothing has changed.
                    // The build context must remain "../src" because SparklineProcessor.csproj holds
                    // a ProjectReference to ../SparklineShared, so Docker needs both directories.
                    // The Exclude list keeps the hash (and the Docker context) scoped to only the
                    // two directories that actually affect the publish output.
                    AssetHashType = Amazon.CDK.AssetHashType.SOURCE,
                    Exclude =
                    [
                        // Unrelated Lambda projects — changes here don't affect SparklineProcessor.
                        "CurrentStateCollector", "DisneylandClient", "TestConsole",
                        "ThemeParkApi", "ThemeParkPoller", "WebSocketBroadcaster",
                        // Local build and IDE artefacts.
                        "bin", "obj", ".vs", "*.user", "*.DotSettings.user",
                    ],
                    Bundling = new BundlingOptions
                    {
                        Image   = DockerImage.FromRegistry("public.ecr.aws/sam/build-dotnet10"),
                        Command =
                        [
                            "bash", "-c",
                            "dotnet publish SparklineProcessor/SparklineProcessor.csproj -c Release -r linux-x64 --self-contained true -o /asset-output"
                        ],
                    },
                }),
                MemorySize  = 256,
                Timeout     = Duration.Seconds(30),
                SnapStart   = SnapStartConf.ON_PUBLISHED_VERSIONS,
                Environment = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["SPARKLINE_TABLE_NAME"] = sparklineTable.TableName,
                    ["UPDATES_TOPIC_ARN"]    = props.UpdatesTopic.TopicArn,
                },
            });

            // ── Permissions ───────────────────────────────────────────────────────
            sparklineTable.GrantReadWriteData(processorFn);
            props.UpdatesTopic.GrantPublish(processorFn);

            var processorFnVersion = processorFn.CurrentVersion;

            var processorFnAlias = new Alias(this, "SparklineProcessorFunctionLiveAlias", new AliasProps
            {
                AliasName = "live",
                Version   = processorFnVersion,
            });

            // ── SNS subscription: consume attraction_updated, skip all other types ─
            props.UpdatesTopic.AddSubscription(new LambdaSubscription(processorFnAlias, new LambdaSubscriptionProps
            {
                FilterPolicy = new System.Collections.Generic.Dictionary<string, SubscriptionFilter>
                {
                    ["event_type"] = SubscriptionFilter.StringFilter(new StringConditions
                    {
                        Allowlist = ["attraction_updated"],
                    }),
                },
            }));
        }
    }
}

