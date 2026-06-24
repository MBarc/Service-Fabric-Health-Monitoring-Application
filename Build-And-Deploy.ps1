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

.EXAMPLE
    .\Build-And-Deploy.ps1 -Unsecured

    Unsecured cluster: plain-HTTP package, no certificate anywhere. The endpoint serves
    http://<node>:8472/ and the dashboard is reached either directly or through the SF reverse
    proxy at http://<lb>:19081/HealthMonitoring/TRPDashboard/...
#>
param(
    # Subject/CN of the TLS server cert to bind. Required for a secured (HTTPS) deploy; ignored
    # (and not needed) with -Unsecured.
    [string]$CertFindValue = "",

    # Build a plain-HTTP package for an unsecured cluster: no TLS cert is bound and the endpoint is
    # rewritten to http. The dashboard has no auth gate either way, so this only changes the transport.
    [switch]$Unsecured,

    [string]$ClusterEndpoint = "localhost:19000",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    # Optional secured-cluster auth params -- forwarded to Deploy-ServiceFabricApp.ps1.
    [string]$ServerCertThumbprint,
    [string]$ClientCertThumbprint,
    [switch]$UseAAD,

    # Non-interactive Azure AD (Entra) auth for CI/CD - the pipeline's service principal context is
    # used to fetch a cluster token. Forwarded to the deploy script. See its help for details.
    [switch]$UseAADServicePrincipal,
    [string]$AadClusterResource = "",
    [string]$SecurityToken = "",

    # Skip `dotnet publish` and package reassembly; redeploy the existing pkg directory.
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# Validate the secure-vs-unsecured posture before doing any work.
if ($Unsecured) {
    if ($CertFindValue) {
        Write-Host "Note: -CertFindValue is ignored in -Unsecured mode (no TLS cert is bound)." -ForegroundColor Yellow
    }
}
elseif (-not $CertFindValue) {
    throw "-CertFindValue is required: the subject/CN of the TLS server cert to bind. For a plain-HTTP deploy to an unsecured cluster, pass -Unsecured (no cert needed)."
}

# Rewrites an already-assembled package in place for an unsecured (plain HTTP, no cert) deploy:
# flips the endpoint to http and strips the TLS cert binding. Idempotent. The dashboard has no
# SetupEntryPoint or RunAsPolicy, so (unlike the sibling apps) there is nothing else to drop.
function ConvertTo-UnsecuredPackage {
    param(
        [Parameter(Mandatory)][string]$ServiceManifestPath,
        [Parameter(Mandatory)][string]$ApplicationManifestPath
    )
    $ns = 'http://schemas.microsoft.com/2011/01/fabric'

    [xml]$sm = Get-Content -LiteralPath $ServiceManifestPath
    $nsmSm = New-Object System.Xml.XmlNamespaceManager($sm.NameTable)
    $nsmSm.AddNamespace('f', $ns)
    $endpoint = $sm.SelectSingleNode("//f:Endpoint[@Name='ServiceEndpoint']", $nsmSm)
    if (-not $endpoint) { throw "ServiceEndpoint not found in $ServiceManifestPath" }
    $endpoint.SetAttribute('Protocol', 'http')
    $sm.Save($ServiceManifestPath)

    # ApplicationManifest: drop the cert binding + the EndpointCertificate it references.
    [xml]$am = Get-Content -LiteralPath $ApplicationManifestPath
    $nsmAm = New-Object System.Xml.XmlNamespaceManager($am.NameTable)
    $nsmAm.AddNamespace('f', $ns)
    $binding = $am.SelectSingleNode("//f:EndpointBindingPolicy", $nsmAm)
    if ($binding) { [void]$binding.ParentNode.RemoveChild($binding) }
    $certs = $am.SelectSingleNode("//f:Certificates", $nsmAm)
    if ($certs) { [void]$certs.ParentNode.RemoveChild($certs) }
    $am.Save($ApplicationManifestPath)
}
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
if ($Unsecured) {
    Write-Host "  Transport:       UNSECURED (plain HTTP, no cert)" -ForegroundColor Yellow
} else {
    Write-Host "  Cert subject:    $CertFindValue" -ForegroundColor Gray
}

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

    if ($Unsecured) {
        Write-Host "Rewriting package for an unsecured (plain HTTP, no cert) deploy..." -ForegroundColor Yellow
        ConvertTo-UnsecuredPackage `
            -ServiceManifestPath (Join-Path $svcPkgDir "ServiceManifest.xml") `
            -ApplicationManifestPath (Join-Path $pkgDir "ApplicationManifest.xml")
    }
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
if ($ServerCertThumbprint)   { $deployArgs.ServerCertThumbprint   = $ServerCertThumbprint }
if ($ClientCertThumbprint)   { $deployArgs.ClientCertThumbprint   = $ClientCertThumbprint }
if ($UseAAD)                 { $deployArgs.UseAAD = $true }
if ($UseAADServicePrincipal) { $deployArgs.UseAADServicePrincipal = $true }
if ($AadClusterResource)     { $deployArgs.AadClusterResource     = $AadClusterResource }
if ($SecurityToken)          { $deployArgs.SecurityToken          = $SecurityToken }
if ($Unsecured)              { $deployArgs.Unsecured = $true }

& (Join-Path $scriptDir "Deploy-ServiceFabricApp.ps1") @deployArgs
