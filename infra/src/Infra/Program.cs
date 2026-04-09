using System;
using Amazon.CDK;

namespace Infra;

sealed class Program
{
    public static void Main(string[] args)
    {
        var app = new App();
        Tags.Of(app).Add("Application", "DisneylandTracker");

        var account       = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT");
        var defaultRegion = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION");

        // ── Domain configuration (resolved from CDK context; never hardcoded) ──
        // Supply values in one of three ways — see infra/README.md for full details.
        //
        //   Option 1 — command-line flags (good for CI/CD):
        //     cdk deploy -c frontEndDomain=app.example.com -c hostedZoneDomain=example.com
        //
        //   Option 2 — local cdk.context.json (good for everyday dev; file is gitignored):
        //     { "frontEndDomain": "app.example.com", "hostedZoneDomain": "example.com" }
        //
        //   Option 3 — "context" block in your local cdk.json (if you have a personal fork):
        //     Copy cdk.json.example → cdk.json and fill in the placeholders.
        var frontEndDomain = app.Node.TryGetContext("frontEndDomain") as string
            ?? throw new Exception(
                "CDK context key 'frontEndDomain' is required (e.g. 'app.example.com'). " +
                "Pass it with -c frontEndDomain=<value> or add it to a local cdk.context.json. " +
                "See infra/README.md for details.");

        var hostedZoneDomain = app.Node.TryGetContext("hostedZoneDomain") as string
            ?? throw new Exception(
                "CDK context key 'hostedZoneDomain' is required (e.g. 'example.com'). " +
                "Pass it with -c hostedZoneDomain=<value> or add it to a local cdk.context.json. " +
                "See infra/README.md for details.");

        var domain = new DomainConfig(frontEndDomain, hostedZoneDomain);

        var pollerStack = new PollerStack(app, "PollerStack", new StackProps
        {
            Env = new Amazon.CDK.Environment { Account = account, Region = defaultRegion }
        });

        var sparklineStack = new SparklineStack(app, "SparklineStack", new SparklineStackProps
        {
            Env          = new Amazon.CDK.Environment { Account = account, Region = defaultRegion },
            UpdatesTopic = pollerStack.UpdatesTopic,
        });

        var apiStack = new ApiStack(app, "ApiStack", new ApiStackProps
        {
            Env               = new Amazon.CDK.Environment { Account = account, Region = defaultRegion },
            CurrentStateTable = pollerStack.CurrentStateTable,
            SparklineTable    = sparklineStack.SparklineTable,
            Domain            = domain,
        });

        var webSocketStack = new WebSocketStack(app, "WebSocketStack", new WebSocketStackProps
        {
            Env          = new Amazon.CDK.Environment { Account = account, Region = defaultRegion },
            UpdatesTopic = pollerStack.UpdatesTopic,
        });

        // ── Front-end stack (CloudFront + S3 + Route53 + ACM certificate) ──────
        var frontEndStack = new FrontEndStack(app, "FrontEndStack", new FrontEndStackProps
        {
            Env    = new Amazon.CDK.Environment { Account = account, Region = defaultRegion },
            Domain = domain,
        });

        app.Synth();
    }
}