<#
.SYNOPSIS
    Build, package, and deploy the Service Fabric Health Dashboard in one step.

.DESCRIPTION
    Bootstrap script for fresh clones and quick rebuild-redeploy loops.
    Runs `dotnet publish` on TRPDashboard.csproj, hand-assembles the Service
    Fabric application package layout, and invokes Deploy-ServiceFabricApp.ps1
    against the target cluster.

    Passes all secured-cluster auth params straight through to the deploy
    script, so the same invocation works for local dev, X509-secured, and
    AAD-secured clusters.

.EXAMPLE
    .\Build-And-Deploy.ps1 -CertFindValue "localhost"

    Local dev cluster with a self-signed cert whose subject is CN=localhost.

.EXAMPLE
    .\Build-And-Deploy.ps1 `
        -ClusterEndpoint "prod-cluster.example.com:19000" `
        -CertFindValue "prod-cluster.example.com" `
        -ServerCertThumbprint "ABCD..." `
        -ClientCertThumbprint "EFGH..."

    Production X509-secured cluster.

.EXAMPLE
    .\Build-And-Deploy.ps1 -CertFindValue "localhost" -SkipBuild

    Re-deploy whatever is already in pkg\Debug\ without rebuilding.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$CertFindValue,

    [string]$ClusterEndpoint = "localhost:19000",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    # Optional secured-cluster auth params -- forwarded to Deploy-ServiceFabricApp.ps1.
    [string]$ServerCertThumbprint,
    [string]$ClientCertThumbprint,
    [switch]$UseAAD,

    # Skip `dotnet publish` and package reassembly; redeploy the existing pkg directory.
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$svcProj     = Join-Path $scriptDir "TRPDashboard\TRPDashboard.csproj"
$svcPkgRoot  = Join-Path $scriptDir "TRPDashboard\PackageRoot"
$appPkgRoot  = Join-Path $scriptDir "HealthMonitoring\ApplicationPackageRoot"
$pkgDir      = Join-Path $scriptDir "HealthMonitoring\pkg\$Configuration"
$svcPkgDir   = Join-Path $pkgDir "TRPDashboardPkg"
$codeDir     = Join-Path $svcPkgDir "Code"

Write-Host "=== Build and Deploy ===" -ForegroundColor Cyan
Write-Host "  Configuration:   $Configuration" -ForegroundColor Gray
Write-Host "  Target cluster:  $ClusterEndpoint" -ForegroundColor Gray
Write-Host "  Cert subject:    $CertFindValue" -ForegroundColor Gray

if (-not $SkipBuild) {
    Write-Host "`n--- Build ---" -ForegroundColor Cyan

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet CLI not found on PATH. Install the .NET 10 SDK first: winget install Microsoft.DotNet.SDK.10"
    }

    if (Test-Path $codeDir) {
        Write-Host "Cleaning $codeDir..." -ForegroundColor Yellow
        Remove-Item $codeDir -Recurse -Force
    }

    # Also clear any stray zipped packages that legacy SF tooling may have left behind.
    if (Test-Path $svcPkgDir) {
        Get-ChildItem $svcPkgDir -Filter "*.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
    }

    Write-Host "Publishing TRPDashboard (self-contained, win-x64)..." -ForegroundColor Yellow
    dotnet publish $svcProj -c $Configuration -r win-x64 --self-contained true -o $codeDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    Write-Host "Assembling SF package layout..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $svcPkgDir | Out-Null
    Copy-Item (Join-Path $svcPkgRoot "ServiceManifest.xml") -Destination $svcPkgDir -Force
    Copy-Item (Join-Path $appPkgRoot "ApplicationManifest.xml") -Destination $pkgDir -Force
}
else {
    Write-Host "`n--- Build skipped (-SkipBuild) ---" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $pkgDir "ApplicationManifest.xml"))) {
        throw "Cannot skip build -- no existing package at $pkgDir. Run without -SkipBuild first."
    }
}

Write-Host "`n--- Deploy ---" -ForegroundColor Cyan
$deployArgs = @{
    ClusterEndpoint = $ClusterEndpoint
    Configuration   = $Configuration
    CertFindValue   = $CertFindValue
}
if ($ServerCertThumbprint) { $deployArgs.ServerCertThumbprint = $ServerCertThumbprint }
if ($ClientCertThumbprint) { $deployArgs.ClientCertThumbprint = $ClientCertThumbprint }
if ($UseAAD)               { $deployArgs.UseAAD = $true }

& (Join-Path $scriptDir "Deploy-ServiceFabricApp.ps1") @deployArgs
