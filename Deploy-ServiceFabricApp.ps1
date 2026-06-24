# Service Fabric Application Deployment Script
# Place this script at the root level (same level as HealthMonitoring and TRPDashboard folders)

param(
    [string]$Configuration = "Debug",
    [string]$AppName = "fabric:/HealthMonitoring",
    [string]$AppTypeName = "HealthMonitoringType",
    [string]$ClusterEndpoint = "localhost:19000",

    # Subject name (CN value) of the TLS cert that SF should bind to the dashboard's
    # 8472 endpoint. SF does substring matching against certs in LocalMachine\My on
    # the node where the service activates, then picks the most recent non-expired
    # match. Examples: "localhost" (dev), "mycluster.example.com" (prod).
    # The cert must exist in LocalMachine\My on every cluster node.
    # Required for a secured (HTTPS) deploy; ignored (and not needed) with -Unsecured.
    [string]$CertFindValue = "",

    # Deploy a plain-HTTP package built for an unsecured cluster (no TLS cert). The package
    # must have been assembled with Build-And-Deploy.ps1 -Unsecured (which rewrites the endpoint
    # to http and strips the cert binding). The dashboard has no auth gate, so this only changes
    # the transport. The dashboard is then UNENCRYPTED. Dev / isolated clusters only.
    [switch]$Unsecured,

    # ------------------------------------------------------------------
    # Optional cluster-auth params. Omit all three for an unsecured cluster
    # (local dev clusters and some on-prem setups). For secured clusters,
    # supply exactly ONE of the auth modes below plus -ServerCertThumbprint.
    # ------------------------------------------------------------------

    # Thumbprint of the cluster's server certificate. Required for both X509
    # and AAD authentication; the deploy client validates the cluster's TLS
    # cert against this. Comma-separated if the cluster presents multiple.
    [string]$ServerCertThumbprint,

    # Thumbprint of YOUR client certificate (X509 auth mode). The cert must
    # exist in CurrentUser\My or LocalMachine\My on the machine running this
    # script (we check both, CurrentUser first), and be trusted by the target
    # cluster. Mutually exclusive with -UseAAD.
    [string]$ClientCertThumbprint,

    # Use Azure Active Directory authentication instead of client cert.
    # Triggers an INTERACTIVE sign-in (browser pop-up) the first time, then
    # caches the token. For non-interactive CI/CD use -UseAADServicePrincipal.
    # Mutually exclusive with -ClientCertThumbprint and -UseAADServicePrincipal.
    [switch]$UseAAD,

    # Non-interactive Azure AD (Entra) auth for CI/CD - Octopus, Azure DevOps, GitHub Actions.
    # The pipeline has already signed in a service principal (an Az / Azure CLI context exists);
    # this pulls a cluster-scoped token from that context and connects with it, so the SP secret
    # or cert never touches this script (and OIDC / workload-identity federation works too).
    # Requires -ServerCertThumbprint. Mutually exclusive with -UseAAD and -ClientCertThumbprint.
    [switch]$UseAADServicePrincipal,

    # The cluster's AAD resource (App ID URI / audience the admin AD group is assigned to). Leave
    # blank to auto-discover it from the cluster's anonymous GetAadMetadata endpoint.
    [string]$AadClusterResource = "",

    # Escape hatch: a pre-acquired AAD bearer token to connect with, skipping discovery + acquisition.
    [string]$SecurityToken = ""
)

# Validate auth combinations early so we fail with a clear message rather
# than a cryptic Connect-ServiceFabricCluster error 30 seconds in.
if ($UseAAD -and $ClientCertThumbprint) {
    throw "Cannot combine -UseAAD with -ClientCertThumbprint. Pick one auth mode."
}
if (($UseAAD -or $ClientCertThumbprint) -and -not $ServerCertThumbprint) {
    throw "Secured-cluster auth requires -ServerCertThumbprint so the deploy client can validate the cluster's TLS cert."
}
# Non-interactive AAD (service principal) is one of the auth modes, not a modifier - it can't be
# combined with the others, and it still needs the server cert thumbprint for TLS validation.
if ($UseAADServicePrincipal -and ($UseAAD -or $ClientCertThumbprint)) {
    throw "Pick one auth mode: -UseAADServicePrincipal (non-interactive AAD for CI/CD), -UseAAD (interactive AAD), or -ClientCertThumbprint (X509)."
}
if ($UseAADServicePrincipal -and -not $ServerCertThumbprint) {
    throw "-UseAADServicePrincipal requires -ServerCertThumbprint so the deploy client can validate the cluster's TLS cert."
}
# -Unsecured is about the app's own transport (plain HTTP, no cert); it is independent of how we
# authenticate to the cluster above. A secured (HTTPS) deploy still needs the endpoint cert subject.
if (-not $Unsecured -and -not $CertFindValue) {
    throw "-CertFindValue is required for a secured (HTTPS) deployment. For a plain-HTTP deploy to an unsecured cluster, pass -Unsecured (and build the package with Build-And-Deploy.ps1 -Unsecured)."
}

# Get script location
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== Service Fabric Deployment Script ===" -ForegroundColor Cyan
Write-Host "Script location: $scriptDir" -ForegroundColor Gray
Write-Host "Configuration: $Configuration" -ForegroundColor Gray
Write-Host "Target cluster: $ClusterEndpoint" -ForegroundColor Gray

# Look for the Service Fabric application project (HealthMonitoring folder)
$appProjectPath = Join-Path $scriptDir "HealthMonitoring"
if (-not (Test-Path $appProjectPath)) {
    Write-Host "ERROR: Could not find HealthMonitoring folder!" -ForegroundColor Red
    Write-Host "Make sure this script is placed at the root level with HealthMonitoring and TRPDashboard folders." -ForegroundColor Red
    exit 1
}

# Look for the pkg folder (created after building the Service Fabric project)
$packagePath = Join-Path $appProjectPath "pkg\$Configuration"
if (-not (Test-Path $packagePath)) {
    Write-Host "ERROR: Could not find application package at: $packagePath" -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "You need to build the Service Fabric project first!" -ForegroundColor Yellow
    Write-Host "To build:" -ForegroundColor Yellow
    Write-Host "1. Open HealthMonitoring.sln in Visual Studio" -ForegroundColor Yellow
    Write-Host "2. Right-click 'HealthMonitoring' project → Build" -ForegroundColor Yellow
    Write-Host "3. This will create the pkg folder with the deployment package" -ForegroundColor Yellow
    Write-Host "" -ForegroundColor Yellow
    Write-Host "Alternative using MSBuild:" -ForegroundColor Yellow
    Write-Host "msbuild HealthMonitoring\HealthMonitoring.sfproj /p:Configuration=$Configuration" -ForegroundColor Yellow
    exit 1
}

# Verify ApplicationManifest.xml exists in the package
$manifestPath = Join-Path $packagePath "ApplicationManifest.xml"
if (-not (Test-Path $manifestPath)) {
    Write-Host "ERROR: ApplicationManifest.xml not found in package!" -ForegroundColor Red
    Write-Host "Package path: $packagePath" -ForegroundColor Red
    Write-Host "The build may have failed. Check Visual Studio output for errors." -ForegroundColor Red
    exit 1
}

Write-Host "Found application package at: $packagePath" -ForegroundColor Green

# Verify service manifest exists (check for TRPDashboard service)
$servicePackagePath = Join-Path $packagePath "TRPDashboardPkg"
if (-not (Test-Path $servicePackagePath)) {
    Write-Host "ERROR: TRPDashboardPkg not found in application package!" -ForegroundColor Red
    Write-Host "Expected path: $servicePackagePath" -ForegroundColor Red
    Write-Host "Make sure the TRPDashboard service is properly referenced in the HealthMonitoring project." -ForegroundColor Red
    exit 1
}

$serviceManifestPath = Join-Path $servicePackagePath "ServiceManifest.xml"
if (-not (Test-Path $serviceManifestPath)) {
    Write-Host "ERROR: ServiceManifest.xml not found for TRPDashboard service!" -ForegroundColor Red
    Write-Host "Expected path: $serviceManifestPath" -ForegroundColor Red
    exit 1
}

Write-Host "Found TRPDashboard service package." -ForegroundColor Green

# Read version from ApplicationManifest.xml
Write-Host "`nReading application version from manifest..." -ForegroundColor Yellow
try {
    [xml]$manifest = Get-Content $manifestPath
    $version = $manifest.ApplicationManifest.ApplicationTypeVersion
    $appTypeNameFromManifest = $manifest.ApplicationManifest.ApplicationTypeName
    Write-Host "Detected version: $version" -ForegroundColor Green
    Write-Host "Application type: $appTypeNameFromManifest" -ForegroundColor Green
    
    # Use the app type name from manifest
    if ($appTypeNameFromManifest -and $appTypeNameFromManifest -ne $AppTypeName) {
        Write-Host "Using application type name from manifest: $appTypeNameFromManifest" -ForegroundColor Yellow
        $AppTypeName = $appTypeNameFromManifest
    }
} catch {
    Write-Host "ERROR: Could not read ApplicationManifest.xml!" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

# --- Azure AD (Entra) non-interactive auth helpers (CI/CD service principal) -------------------
# The cluster token must be scoped to the cluster's own Entra app (its App ID URI / audience).
# Service Fabric publishes that anonymously at the HTTP gateway, so we discover it rather than
# hardcoding. TLS to the gateway is pinned to -ServerCertThumbprint when the cert isn't already trusted.
function Resolve-SfAadResource {
    param(
        [Parameter(Mandatory)][string]$ClusterEndpoint,
        [string]$ServerCertThumbprint
    )
    $hostName = ($ClusterEndpoint -split ':')[0]
    $metaUrl  = "https://${hostName}:19080/`$/GetAadMetadata?api-version=1.0"

    $prev = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
    if ($ServerCertThumbprint) {
        $allowed = @($ServerCertThumbprint -split '[,;]' | ForEach-Object { ($_ -replace '[^0-9A-Fa-f]','').ToUpper() } | Where-Object { $_ })
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = {
            param($snd, $cert, $chain, $sslErrors)
            if ($sslErrors -eq [System.Net.Security.SslPolicyErrors]::None) { return $true }
            return $allowed -contains $cert.GetCertHashString().ToUpper()
        }.GetNewClosure()
    }
    try {
        $meta = Invoke-RestMethod -Uri $metaUrl -Method Get
    } finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $prev
    }
    $resource = $meta.metadata.cluster
    if (-not $resource) {
        throw "GetAadMetadata at $metaUrl did not return a cluster AAD resource. Pass -AadClusterResource explicitly (the cluster app's App ID URI)."
    }
    return $resource
}

# Pulls a token for $Resource from the pipeline's already-authenticated context: the Az PowerShell
# module first (ADO 'Azure PowerShell' task, GHA azure/login, Octopus Az), then the Azure CLI.
function Get-SfAmbientAadToken {
    param([Parameter(Mandatory)][string]$Resource)

    if (Get-Command Get-AzAccessToken -ErrorAction SilentlyContinue) {
        try {
            $t = Get-AzAccessToken -ResourceUrl $Resource -ErrorAction Stop
            $raw = $t.Token
            if ($raw -is [System.Security.SecureString]) {
                $raw = [System.Net.NetworkCredential]::new('', $raw).Password
            }
            if ($raw) { return $raw }
        } catch {
            Write-Host "  Get-AzAccessToken failed ($($_.Exception.Message)); trying Azure CLI..." -ForegroundColor Yellow
        }
    }
    if (Get-Command az -ErrorAction SilentlyContinue) {
        $tok = az account get-access-token --resource $Resource --query accessToken -o tsv 2>$null
        if ($LASTEXITCODE -eq 0 -and $tok) { return $tok.Trim() }
    }
    throw "No ambient Azure context to get a token from. Run the pipeline's Azure login step first (Connect-AzAccount / az login as the service principal); Az.Accounts or the Azure CLI must be on PATH."
}

# Connect to Service Fabric cluster
Write-Host "`nConnecting to Service Fabric cluster: $ClusterEndpoint..." -ForegroundColor Yellow
try {
    if ($UseAADServicePrincipal) {
        Write-Host "  Auth mode: Azure AD service principal (non-interactive; ambient pipeline context)" -ForegroundColor Gray
        $token = $SecurityToken
        if (-not $token) {
            $resource = $AadClusterResource
            if (-not $resource) {
                Write-Host "  Discovering cluster AAD resource via GetAadMetadata..." -ForegroundColor Gray
                $resource = Resolve-SfAadResource -ClusterEndpoint $ClusterEndpoint -ServerCertThumbprint $ServerCertThumbprint
            }
            Write-Host "  Cluster AAD resource: $resource" -ForegroundColor Gray
            $token = Get-SfAmbientAadToken -Resource $resource
        }
        Connect-ServiceFabricCluster -ConnectionEndpoint $ClusterEndpoint `
            -AzureActiveDirectory `
            -ServerCertThumbprint $ServerCertThumbprint `
            -SecurityToken $token | Out-Null
    }
    elseif ($UseAAD) {
        Write-Host "  Auth mode: Azure Active Directory (interactive)" -ForegroundColor Gray
        Connect-ServiceFabricCluster -ConnectionEndpoint $ClusterEndpoint `
            -AzureActiveDirectory `
            -ServerCertThumbprint $ServerCertThumbprint | Out-Null
    }
    elseif ($ClientCertThumbprint) {
        # Locate the client cert. CurrentUser takes precedence (matches the
        # interactive deploy pattern); LocalMachine\My is the fallback for
        # CI/CD where the script runs under a service account.
        $clientCertStore = $null
        if (Test-Path "Cert:\CurrentUser\My\$ClientCertThumbprint") {
            $clientCertStore = "CurrentUser"
        } elseif (Test-Path "Cert:\LocalMachine\My\$ClientCertThumbprint") {
            $clientCertStore = "LocalMachine"
        } else {
            throw "Client certificate with thumbprint $ClientCertThumbprint was not found in CurrentUser\My or LocalMachine\My. Install it (and grant the current user read access to its private key) before deploying."
        }

        Write-Host "  Auth mode: X509 client certificate (from $clientCertStore\My)" -ForegroundColor Gray
        Connect-ServiceFabricCluster -ConnectionEndpoint $ClusterEndpoint `
            -X509Credential `
            -ServerCertThumbprint $ServerCertThumbprint `
            -FindType FindByThumbprint `
            -FindValue $ClientCertThumbprint `
            -StoreLocation $clientCertStore `
            -StoreName My | Out-Null
    }
    else {
        Write-Host "  Auth mode: Unsecured (no credentials)" -ForegroundColor Gray
        Connect-ServiceFabricCluster -ConnectionEndpoint $ClusterEndpoint | Out-Null
    }
    Write-Host "Connected to cluster successfully." -ForegroundColor Green
    
    # Display cluster info
    try {
        $clusterHealth = Get-ServiceFabricClusterHealth
        Write-Host "Cluster Health: $($clusterHealth.AggregatedHealthState)" -ForegroundColor Green
    } catch {
        Write-Host "Connected, but could not retrieve cluster health details." -ForegroundColor Yellow
    }
} catch {
    Write-Host "ERROR: Failed to connect to Service Fabric cluster!" -ForegroundColor Red
    Write-Host "Cluster endpoint: $ClusterEndpoint" -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "Troubleshooting:" -ForegroundColor Yellow
    Write-Host "  - Confirm the cluster endpoint, network reachability, and that the cluster is running." -ForegroundColor Yellow
    Write-Host "  - For a secured cluster, supply -ServerCertThumbprint plus one of -ClientCertThumbprint" -ForegroundColor Yellow
    Write-Host "    (X509 mode) or -UseAAD (Azure AD mode)." -ForegroundColor Yellow
    Write-Host "" -ForegroundColor Yellow
    Write-Host "Error details: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# Remove existing application if it exists
Write-Host "`nCleaning up existing application..." -ForegroundColor Yellow
try {
    $existingApp = Get-ServiceFabricApplication -ApplicationName $AppName -ErrorAction SilentlyContinue
    if ($existingApp) {
        Write-Host "Removing existing application: $AppName" -ForegroundColor Yellow
        Remove-ServiceFabricApplication -ApplicationName $AppName -Force
        
        # Wait for removal to complete
        $attempts = 0
        do {
            Start-Sleep -Seconds 2
            $existingApp = Get-ServiceFabricApplication -ApplicationName $AppName -ErrorAction SilentlyContinue
            $attempts++
            if ($attempts % 5 -eq 0) {
                Write-Host "Still waiting for application removal... (attempt $attempts)" -ForegroundColor Gray
            }
        } while ($existingApp -and $attempts -lt 30)
        
        if ($existingApp) {
            Write-Host "Warning: Application removal took longer than expected." -ForegroundColor Yellow
        } else {
            Write-Host "Application removed successfully." -ForegroundColor Green
        }
    }
    
    # Clean up all versions of the application type
    $existingTypes = Get-ServiceFabricApplicationType -ApplicationTypeName $AppTypeName -ErrorAction SilentlyContinue
    foreach ($existingType in $existingTypes) {
        Write-Host "Unregistering application type: $AppTypeName v$($existingType.ApplicationTypeVersion)" -ForegroundColor Yellow
        try {
            Unregister-ServiceFabricApplicationType -ApplicationTypeName $AppTypeName -ApplicationTypeVersion $existingType.ApplicationTypeVersion -Force
        } catch {
            Write-Host "Warning: Could not unregister version $($existingType.ApplicationTypeVersion)" -ForegroundColor Yellow
        }
    }
    
    if ($existingTypes) {
        Write-Host "Application types unregistered successfully." -ForegroundColor Green
    }
} catch {
    Write-Host "Note: No existing application to clean up" -ForegroundColor Gray
}

# Deploy application
Write-Host "`nDeploying application package..." -ForegroundColor Yellow
try {
    $imageStorePath = "${AppTypeName}_${version}_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
    Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $packagePath -ApplicationPackagePathInImageStore $imageStorePath -ShowProgress
    Write-Host "Application package uploaded successfully." -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to upload application package!" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    
    # Check for common issues
    if ($_.Exception.Message -like "*E_FAIL*" -or $_.Exception.Message -like "*Download*") {
        Write-Host "`nThis might be the E_FAIL issue you encountered before." -ForegroundColor Yellow
        Write-Host "Troubleshooting tips:" -ForegroundColor Yellow
        Write-Host "1. Try restarting Service Fabric Local Cluster (right-click tray icon → Reset Local Cluster)" -ForegroundColor Yellow
        Write-Host "2. Rebuild the HealthMonitoring project completely" -ForegroundColor Yellow
        Write-Host "3. Check that all files exist in: $packagePath" -ForegroundColor Yellow
        Write-Host "4. Verify TRPDashboard.exe exists in: $servicePackagePath\Code\" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "`nRegistering application type..." -ForegroundColor Yellow
try {
    Register-ServiceFabricApplicationType -ApplicationPathInImageStore $imageStorePath
    Write-Host "Application type registered successfully." -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to register application type!" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

Write-Host "`nCreating application instance..." -ForegroundColor Yellow
if ($Unsecured) {
    Write-Host "  Transport:         UNSECURED (plain HTTP, no cert)" -ForegroundColor Yellow
} else {
    Write-Host "  Cert subject name: $CertFindValue" -ForegroundColor Gray
}
try {
    $appParams = @{ CertFindValue = $CertFindValue }
    New-ServiceFabricApplication -ApplicationName $AppName -ApplicationTypeName $AppTypeName -ApplicationTypeVersion $version -ApplicationParameter $appParams
    Write-Host "Application created successfully." -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to create application!" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

# Clean up image store
Write-Host "`nCleaning up image store..." -ForegroundColor Yellow
try {
    Remove-ServiceFabricApplicationPackage -ApplicationPackagePathInImageStore $imageStorePath
} catch {
    Write-Host "Warning: Could not clean up image store (this is not critical)." -ForegroundColor Yellow
}

# Display completion info
Write-Host "`n=== Deployment Complete! ===" -ForegroundColor Cyan
Write-Host "Application Name: $AppName" -ForegroundColor Green
Write-Host "Application Type: $AppTypeName v$version" -ForegroundColor Green
Write-Host "Target Cluster: $ClusterEndpoint" -ForegroundColor Green
Write-Host "Service Fabric Explorer: https://$($ClusterEndpoint.Replace(':19000', ':19080'))" -ForegroundColor Green

# Check application status
Write-Host "`nChecking application status..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

try {
    $app = Get-ServiceFabricApplication -ApplicationName $AppName
    Write-Host "Application Status: $($app.ApplicationStatus)" -ForegroundColor Green
    
    $services = Get-ServiceFabricService -ApplicationName $AppName
    foreach ($service in $services) {
        Write-Host "Service: $($service.ServiceName) - Status: $($service.ServiceStatus)" -ForegroundColor Green
    }
    $dashScheme = if ($Unsecured) { "http" } else { "https" }
    Write-Host "`nDashboard (direct, per node): ${dashScheme}://<node>:8472/health-dashboard" -ForegroundColor Cyan
    Write-Host "Dashboard (via SF reverse proxy): ${dashScheme}://<lb-or-node>:19081/HealthMonitoring/TRPDashboard/health-dashboard" -ForegroundColor Gray
} catch {
    Write-Host "Application is still starting up. Check Service Fabric Explorer for details." -ForegroundColor Yellow
}

$explorerUrl = "https://$($ClusterEndpoint.Replace(':19000', ':19080'))"
Write-Host "`nMonitor the deployment in Service Fabric Explorer: $explorerUrl" -ForegroundColor Gray