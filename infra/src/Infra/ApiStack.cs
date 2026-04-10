using Amazon.CDK;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Constructs;

namespace Infra
{
    /// <summary>Props that wire the ApiStack to resources owned by the PollerStack and SparklineStack.</summary>
    public class ApiStackProps : StackProps
    {
        /// <summary>The CurrentStateTable created in <see cref="PollerStack"/>.</summary>
        public ITable CurrentStateTable { get; init; } = null!;

        /// <summary>The SparklineStore table created in <see cref="SparklineStack"/>.</summary>
        public ITable SparklineTable { get; init; } = null!;

        /// <summary>Domain configuration resolved from CDK context. Used to restrict CORS to the frontend origin.</summary>
        public DomainConfig Domain { get; init; } = null!;
    }

    public class ApiStack : Stack
    {
        internal ApiStack(Construct scope, string id, ApiStackProps props) : base(scope, id, props)
        {
            // ── Lambda function ───────────────────────────────────────────────────
            var apiFn = new Function(this, "ThemeParkApiFunction", new FunctionProps
            {
                Description  = "Provides a RESTful interface for Disneyland Resort live attraction data.",
                Runtime      = Runtime.DOTNET_10,
                Architecture = Architecture.X86_64,
                Handler      = "ThemeParkApi::ThemeParkApi.Function::FunctionHandler",
                Code = Code.FromAsset("../src", new Amazon.CDK.AWS.S3.Assets.AssetOptions
                {
                    // SOURCE hashing fingerprints the local .cs/.csproj files before Docker runs,
                    // so `cdk synth` skips the Docker build entirely when nothing has changed.
                    // The build context must remain "../src" because ThemeParkApi.csproj holds
                    // a ProjectReference to ../SparklineShared, so Docker needs both directories.
                    // The Exclude list keeps the hash (and the Docker context) scoped to only the
                    // two directories that actually affect the publish output.
                    AssetHashType = Amazon.CDK.AssetHashType.SOURCE,
                    Exclude =
                    [
                        // Unrelated Lambda projects — changes here don't affect ThemeParkApi.
                        "CurrentStateCollector", "DisneylandClient", "SparklineProcessor",
                        "TestConsole", "ThemeParkPoller", "WebSocketBroadcaster",
                        // Local build and IDE artefacts.
                        "bin", "obj", ".vs", "*.user", "*.DotSettings.user",
                    ],
                    Bundling = new BundlingOptions
                    {
                        // public.ecr.aws/sam/build-dotnet10 provides the .NET 10 SDK on AL2023.
                        Image = DockerImage.FromRegistry("public.ecr.aws/sam/build-dotnet10"),
                        Command =
                        [
                            "bash", "-c",
                            "dotnet publish ThemeParkApi/ThemeParkApi.csproj -c Release -r linux-x64 --self-contained true -o /asset-output"
                        ],
                    },
                }),
                MemorySize  = 256,
                Timeout     = Duration.Seconds(30),
                //SnapStart   = SnapStartConf.ON_PUBLISHED_VERSIONS,
                Environment = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["STATE_TABLE_NAME"]    = props.CurrentStateTable.TableName,
                    ["SPARKLINE_TABLE_NAME"] = props.SparklineTable.TableName,
                },
            });

            // ── Version & Alias ───────────────────────────────────────────────────
            var apiFnVersion = apiFn.CurrentVersion;

            var apiFnAlias = new Alias(this, "ThemeParkApiFunctionLiveAlias", new AliasProps
            {
                AliasName = "live",
                Version   = apiFnVersion,
            });

            // ── Permissions ───────────────────────────────────────────────────────
            // GrantReadData covers the table and all its GSIs.
            props.CurrentStateTable.GrantReadData(apiFn);
            props.SparklineTable.GrantReadData(apiFn);

            // ── REST API ──────────────────────────────────────────────────────────
            var api = new RestApi(this, "ThemeParkApi", new RestApiProps
            {
                RestApiName   = "ThemeParkApi",
                Description   = "REST API for querying live Disneyland Resort attraction data.",
                DeployOptions = new StageOptions { StageName = "prod" },
                DefaultCorsPreflightOptions = new CorsOptions
                {
                    AllowOrigins = new[] { $"https://{props.Domain.FrontEndDomain}" },
                    AllowMethods = new[] { "GET", "OPTIONS" },
                    AllowHeaders = new[]
                    {
                        "Content-Type",
                        "X-Amz-Date",
                        "Authorization",
                        "X-Api-Key",
                        "X-Amz-Security-Token",
                    },
                },
            });

            var integration = new LambdaIntegration(apiFnAlias);

            // GET /attractions/lightning-lane
            // GET /attractions/status/{status}
            var attractions = api.Root.AddResource("attractions");
            attractions.AddResource("lightning-lane").AddMethod("GET", integration);
            attractions.AddResource("status").AddResource("{status}").AddMethod("GET", integration);

            // GET /sparklines/lightning-lane
            api.Root.AddResource("sparklines")
                    .AddResource("lightning-lane")
                    .AddMethod("GET", integration);
        }
    }
}