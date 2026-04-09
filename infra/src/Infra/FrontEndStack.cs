using Amazon.CDK;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace Infra;

/// <summary>
/// Domain configuration resolved from CDK context at synth time.
/// Values are never hardcoded; they are supplied via the <c>-c</c> flag,
/// a local <c>cdk.context.json</c>, or the <c>"context"</c> block in <c>cdk.json</c>.
/// </summary>
public sealed record DomainConfig(
    /// <summary>The fully-qualified subdomain served by CloudFront (e.g. <c>app.example.com</c>).</summary>
    string FrontEndDomain,
    /// <summary>The root Route 53 hosted zone that owns the subdomain (e.g. <c>example.com</c>).</summary>
    string HostedZoneDomain);

/// <summary>Props that supply domain configuration to <see cref="FrontEndStack"/>.</summary>
public class FrontEndStackProps : StackProps
{
    /// <summary>Domain names resolved from CDK context. See <c>cdk.json.example</c> for the required keys.</summary>
    public DomainConfig Domain { get; init; } = null!;
}

public class FrontEndStack : Stack
{
    public FrontEndStack(Construct scope, string id, FrontEndStackProps props) : base(scope, id, props)
    {
        var domain = props.Domain;

        // ── S3 bucket (private; CloudFront is the sole origin) ────────────────
        var bucket = new Bucket(this, "ThemeParkFrontEndBucket", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            RemovalPolicy     = RemovalPolicy.DESTROY,
            AutoDeleteObjects  = true,
        });

        // ── Route53 hosted zone + ACM certificate ─────────────────────────────
        // The hosted zone lookup runs at CDK synth time and its result is cached
        // in cdk.context.json (gitignored — see infra/README.md).
        var hostedZone = HostedZone.FromLookup(this, "HostedZone", new HostedZoneProviderProps
        {
            DomainName = domain.HostedZoneDomain,
        });

        var certificate = new Certificate(this, "FrontEndCertificate", new CertificateProps
        {
            DomainName = domain.FrontEndDomain,
            Validation = CertificateValidation.FromDns(hostedZone),
        });

        // ── CloudFront distribution ───────────────────────────────────────────
        // S3BucketOrigin.WithOriginAccessControl creates an OAC and the bucket
        // policy that allows CloudFront to read, keeping the bucket fully private.
        var distribution = new Distribution(this, "ThemeParkFrontEndDistribution", new DistributionProps
        {
            DefaultRootObject = "index.html",
            DefaultBehavior   = new BehaviorOptions
            {
                Origin               = S3BucketOrigin.WithOriginAccessControl(bucket),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
            },
            // Serve the custom domain; CloudFront requires the matching ACM certificate.
            DomainNames  = new[] { domain.FrontEndDomain },
            Certificate  = certificate,
            // Return index.html for 404s so Blazor WASM client-side routing works.
            ErrorResponses = new[]
            {
                new ErrorResponse
                {
                    HttpStatus         = 404,
                    ResponseHttpStatus = 200,
                    ResponsePagePath   = "/index.html",
                },
            },
        });

        // Create an A-alias record so <frontEndDomain> → CloudFront distribution.
        new ARecord(this, "FrontEndAliasRecord", new ARecordProps
        {
            Zone       = hostedZone,
            RecordName = domain.FrontEndDomain,
            Target     = RecordTarget.FromAlias(new CloudFrontTarget(distribution)),
        });

        // ── Outputs (consumed by deploy-frontend.ps1) ─────────────────────────
        new CfnOutput(this, "FrontEndBucketName", new CfnOutputProps
        {
            Value       = bucket.BucketName,
            Description = "S3 bucket that hosts the DisneylandClient static assets.",
        });

        new CfnOutput(this, "FrontEndDistributionId", new CfnOutputProps
        {
            Value       = distribution.DistributionId,
            Description = "CloudFront Distribution ID used for cache invalidation after each deploy.",
        });

        new CfnOutput(this, "FrontEndUrl", new CfnOutputProps
        {
            Value       = $"https://{domain.FrontEndDomain}",
            Description = "CloudFront Distribution URL for the DisneylandClient Blazor WebAssembly frontend.",
        });
    }
}
