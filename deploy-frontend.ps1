<#
.SYNOPSIS
    Builds the DisneylandClient Blazor WASM app and deploys it to the S3/CloudFront
    infrastructure created by the CDK FrontEndStack.

.DESCRIPTION
    Performs four sequential steps:
      1. dotnet publish  – produces the static wwwroot assets.
      2. CF discovery    – reads the S3 bucket name and CloudFront Distribution ID
                           from the FrontEndStack CloudFormation outputs.
      3. aws s3 sync     – uploads/removes files so S3 matches the local wwwroot.
      4. CF invalidation – purges the CloudFront edge cache.

.PARAMETER StackName
    Name of the CloudFormation stack that owns the FrontEndStack outputs.
    Defaults to "FrontEndStack".

.EXAMPLE
    .\deploy-frontend.ps1
    .\deploy-frontend.ps1 -StackName "FrontEndStack-staging"
#>
[CmdletBinding()]
param (
    [string] $StackName = "FrontEndStack"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot   = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot "src\DisneylandClient\DisneylandClient.csproj"
$PublishDir  = Join-Path $RepoRoot "src\DisneylandClient\publish"
$WwwrootDir  = Join-Path $PublishDir "wwwroot"

# ── Step 1: Build ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [1/4] Building DisneylandClient..." -ForegroundColor Cyan

dotnet publish $ProjectPath -c Release -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit code $LASTEXITCODE). Aborting."
    exit $LASTEXITCODE
}

if (-not (Test-Path $WwwrootDir)) {
    Write-Error "Expected publish output not found at '$WwwrootDir'. Aborting."
    exit 1
}

Write-Host "    Build succeeded -> $WwwrootDir" -ForegroundColor Green

# ── Step 2: Read stack outputs ────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [2/4] Reading '$StackName' CloudFormation outputs..." -ForegroundColor Cyan

$OutputsJson = aws cloudformation describe-stacks `
    --stack-name $StackName `
    --query "Stacks[0].Outputs" `
    --output json

if ($LASTEXITCODE -ne 0) {
    Write-Error "aws cloudformation describe-stacks failed. Is '$StackName' deployed and are your AWS credentials configured?"
    exit $LASTEXITCODE
}

$Outputs = $OutputsJson | ConvertFrom-Json

function Get-StackOutput([string] $Key) {
    $entry = $Outputs | Where-Object { $_.OutputKey -eq $Key }
    if (-not $entry) {
        Write-Error "Output key '$Key' not found in stack '$StackName'. Re-run 'cdk deploy' to ensure outputs are present."
        exit 1
    }
    return $entry.OutputValue
}

$BucketName     = Get-StackOutput "FrontEndBucketName"
$DistributionId = Get-StackOutput "FrontEndDistributionId"
$FrontEndUrl    = Get-StackOutput "FrontEndUrl"

Write-Host "    Bucket:          $BucketName"       -ForegroundColor Green
Write-Host "    Distribution ID: $DistributionId"   -ForegroundColor Green
Write-Host "    Frontend URL:    $FrontEndUrl"       -ForegroundColor Green

# ── Step 3: Sync assets to S3 ─────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [3/4] Syncing assets to s3://$BucketName/ ..." -ForegroundColor Cyan

aws s3 sync $WwwrootDir "s3://$BucketName/" --delete

if ($LASTEXITCODE -ne 0) {
    Write-Error "aws s3 sync failed (exit code $LASTEXITCODE). Aborting."
    exit $LASTEXITCODE
}

Write-Host "    Sync complete." -ForegroundColor Green

# ── Step 4: CloudFront cache invalidation ─────────────────────────────────────
Write-Host ""
Write-Host "==> [4/4] Creating CloudFront invalidation for distribution '$DistributionId'..." -ForegroundColor Cyan

aws cloudfront create-invalidation `
    --distribution-id $DistributionId `
    --paths "/*"

if ($LASTEXITCODE -ne 0) {
    Write-Error "aws cloudfront create-invalidation failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host "    Invalidation submitted." -ForegroundColor Green

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Deployment complete. Frontend is live at: $FrontEndUrl" -ForegroundColor Green

