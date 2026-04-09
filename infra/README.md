# DisneylandTracker — CDK Infrastructure

AWS CDK (C#) project that provisions the full DisneylandTracker backend:
S3 + CloudFront frontend, ACM certificate, Route 53 alias record, REST API
Gateway (Lambda), WebSocket API Gateway (Lambda), DynamoDB tables, and SNS
topics/subscriptions.

---

## Prerequisites

| Tool | Minimum version |
|---|---|
| .NET SDK | 8.0 |
| AWS CDK Toolkit | 2.x (`npm i -g aws-cdk`) |
| AWS CLI | configured with `CDK_DEFAULT_ACCOUNT` / `CDK_DEFAULT_REGION` |

---

## Domain configuration

The two domain values below are **required at synth time** and are intentionally
absent from `cdk.json` so that personal domain names are never committed to
version control.

| Context key | Example value | Purpose |
|---|---|---|
| `frontEndDomain` | `app.example.com` | Custom domain attached to the CloudFront distribution and used as the CORS `AllowOrigin` on the REST API. |
| `hostedZoneDomain` | `example.com` | Root Route 53 hosted zone that owns the subdomain above. |

### Option 1 — `cdk.context.json` (recommended for local development)

Create `infra/cdk.context.json` (already listed in `.gitignore`) and add the
two keys:

```json
{
  "frontEndDomain":   "app.example.com",
  "hostedZoneDomain": "example.com"
}
```

CDK reads this file automatically on every `cdk synth` / `cdk deploy`.
Because it is gitignored, your domain names stay local.

> **Note on lookup caching:** CDK also writes the results of `HostedZone.FromLookup`
> calls into `cdk.context.json`. Gitignoring this file means those cached results
> are local-only, so the first `cdk synth` after a clean clone will perform a live
> AWS API call to resolve the hosted zone. This is a one-time cost per machine.

### Option 2 — `-c` flags (recommended for CI/CD)

Pass the values on the command line; they are never written to disk:

```bash
cdk deploy \
  -c frontEndDomain=app.example.com \
  -c hostedZoneDomain=example.com
```

### Option 3 — `cdk.json` context block (personal forks only)

If you are working in a private fork and are comfortable committing these
values, merge the keys into the `"context"` object in `cdk.json`:

```json
{
  "context": {
    "frontEndDomain":   "app.example.com",
    "hostedZoneDomain": "example.com"
  }
}
```

⚠️ `cdk.json` **is** committed to version control. Do not use this option in
the shared repository.

A template showing the required keys is provided in `cdk.json.example`.

---

## Useful commands

```bash
# Compile the CDK app
dotnet build src

# Synthesize CloudFormation templates (requires domain context — see above)
cdk synth -c frontEndDomain=app.example.com -c hostedZoneDomain=example.com

# Show a diff against the currently deployed stacks
cdk diff  -c frontEndDomain=app.example.com -c hostedZoneDomain=example.com

# Deploy all stacks
cdk deploy --all -c frontEndDomain=app.example.com -c hostedZoneDomain=example.com

# Deploy a single stack
cdk deploy FrontEndStack -c frontEndDomain=app.example.com -c hostedZoneDomain=example.com
```

If you have populated `cdk.context.json` locally the `-c` flags can be omitted.
