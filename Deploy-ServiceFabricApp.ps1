# Service Fabric Application Deployment Script
# Place this script at the root level (same level as HealthMonitoring and TRPDashboard folders)

param(
    [string]$Configuration = "Debug",
    [string]$AppName = "fabric:/HealthMonitoring",
    [string]$AppTypeName = "HealthMonitoringType",
    [string]$ClusterEndpoint = "hl-svfb-w-t-001:19000"
)

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

# Connect to Service Fabric cluster
Write-Host "`nConnecting to Service Fabric cluster: $ClusterEndpoint..." -ForegroundColor Yellow
try {
    # For unsecured clusters (common in dev/test environments)
    Connect-ServiceFabricCluster -ConnectionEndpoint $ClusterEndpoint | Out-Null
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
    Write-Host "Troubleshooting steps:" -ForegroundColor Yellow
    Write-Host "1. Verify the cluster endpoint is correct: $ClusterEndpoint" -ForegroundColor Yellow
    Write-Host "2. Check if the cluster is running and accessible" -ForegroundColor Yellow
    Write-Host "3. Verify network connectivity to the cluster" -ForegroundColor Yellow
    Write-Host "4. If the cluster uses security, you may need additional connection parameters" -ForegroundColor Yellow
    Write-Host "" -ForegroundColor Yellow
    Write-Host "For secured clusters, you might need to use parameters like:" -ForegroundColor Yellow
    Write-Host "  -X509Credential -ServerCertThumbprint <thumbprint> -FindType FindByThumbprint -FindValue <thumbprint> -StoreLocation CurrentUser -StoreName My" -ForegroundColor Yellow
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
try {
    New-ServiceFabricApplication -ApplicationName $AppName -ApplicationTypeName $AppTypeName -ApplicationTypeVersion $version
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
        
        # Try to get the service endpoint for the dashboard
        if ($service.ServiceName -like "*TRPDashboard*") {
            try {
                $partitions = Get-ServiceFabricPartition -ServiceName $service.ServiceName
                foreach ($partition in $partitions) {
                    $replicas = Get-ServiceFabricReplica -PartitionId $partition.PartitionId
                    foreach ($replica in $replicas) {
                        if ($replica.ReplicaAddress) {
                            $address = $replica.ReplicaAddress | ConvertFrom-Json
                            if ($address.Endpoints -and $address.Endpoints.ServiceEndpoint) {
                                Write-Host "Dashboard URL: $($address.Endpoints.ServiceEndpoint)" -ForegroundColor Cyan
                            }
                        }
                    }
                }
            } catch {
                Write-Host "Service is still starting up..." -ForegroundColor Yellow
                Write-Host "Try accessing the service through the cluster nodes in a few moments" -ForegroundColor Gray
            }
        }
    }
} catch {
    Write-Host "Application is still starting up. Check Service Fabric Explorer for details." -ForegroundColor Yellow
}

$explorerUrl = "https://$($ClusterEndpoint.Replace(':19000', ':19080'))"
Write-Host "`nMonitor the deployment in Service Fabric Explorer: $explorerUrl" -ForegroundColor Gray