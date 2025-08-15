using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TRPDashboard
{
    public class DashboardService
    {
        // Configuration - Department name that can be easily changed
        private const string DEPARTMENT_NAME = "Internal Department Name";

        private readonly FabricClient _fabricClient;
        private readonly StatelessServiceContext _serviceContext;

        public DashboardService(FabricClient fabricClient, StatelessServiceContext serviceContext)
        {
            _fabricClient = fabricClient;
            _serviceContext = serviceContext;
        }

        public async Task<string> GenerateDashboardHtml()
        {
            var currentNode = await GetCurrentNodeAsync();
            var applications = await GetApplicationsAsync();
            var services = await GetServicesAsync();
            var nodes = await GetNodesAsync();
            var serviceFabricVersion = await GetServiceFabricVersionAsync();
            var dotNetVersion = GetDotNetVersion();
            var hardwareInfo = GetHardwareInfo();
            var osInfo = GetOperatingSystemInfo();

            // Handle case where currentNode might be null
            var currentNodeName = currentNode?.NodeName ?? Environment.MachineName;
            var currentNodeIp = GetLocalIPAddress();
            var currentNodeHostname = Environment.MachineName;

            // Format uptime safely
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount);
            var uptimeString = $"{uptime.Days}d {uptime.Hours:D2}h {uptime.Minutes:D2}m {uptime.Seconds:D2}s";

            var html = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Service Fabric Health Dashboard</title>
    <style>
        {GetDashboardStyles()}
    </style>
    <script>
        // Auto-refresh every 30 seconds
        setTimeout(function(){{ location.reload(); }}, 30000);
        
        function copyToClipboard(text) {{
            navigator.clipboard.writeText(text).then(function() {{
                const notification = document.createElement('div');
                notification.textContent = 'Copied: ' + text;
                notification.style.cssText = 'position: fixed; top: 20px; right: 20px; background: #00a651; color: white; padding: 10px 20px; border-radius: 6px; z-index: 1000; font-size: 14px; box-shadow: 0 4px 12px rgba(0,0,0,0.2);';
                document.body.appendChild(notification);
                setTimeout(() => notification.remove(), 3000);
            }});
        }}

        function refreshDashboard() {{
            const button = document.querySelector('.refresh-button');
            button.style.transform = 'rotate(360deg)';
            setTimeout(() => {{ button.style.transform = ''; location.reload(); }}, 500);
        }}
    </script>
</head>
<body>
    <header class='header'>
        <div class='header-content'>
            <div class='department-name'>{DEPARTMENT_NAME}</div>
            <div class='cluster-name'>Service Fabric Cluster</div>
        </div>
    </header>

    <div class='container'>
        <!-- Status Overview Cards -->
        <div class='status-grid'>
            <div class='status-card'>
                <h3>🖥️ Current Node</h3>
                <div class='info-value copyable' onclick='copyToClipboard(""{currentNodeName}"")'>{currentNodeName}</div>
                <div class='info-label'>Handling this request</div>
                <div class='node-details'>
                    <div class='detail-item'>
                        <span class='detail-label'>IP Address:</span>
                        <span class='detail-value copyable' onclick='copyToClipboard(""{currentNodeIp}"")'>{currentNodeIp}</span>
                    </div>
                    <div class='detail-item'>
                        <span class='detail-label'>Hostname:</span>
                        <span class='detail-value copyable' onclick='copyToClipboard(""{currentNodeHostname}"")'>{currentNodeHostname}</span>
                    </div>
                    <div class='detail-item'>
                        <span class='detail-label'>CPU:</span>
                        <span class='detail-value'>{hardwareInfo.CpuCores} cores</span>
                    </div>
                    <div class='detail-item'>
                        <span class='detail-label'>RAM:</span>
                        <span class='detail-value'>{hardwareInfo.TotalMemoryGB:F1} GB</span>
                    </div>
                </div>
            </div>
            
            <div class='status-card'>
                <h3>💾 System Information</h3>
                <div class='info-grid-compact'>
                    <div class='info-item-compact'>
                        <span class='info-label'>Service Fabric:</span>
                        <span class='info-value-small'>{serviceFabricVersion}</span>
                    </div>
                    <div class='info-item-compact'>
                        <span class='info-label'>.NET Version:</span>
                        <span class='info-value-small'>{dotNetVersion}</span>
                    </div>
                    <div class='info-item-compact'>
                        <span class='info-label'>OS:</span>
                        <span class='info-value-small'>{osInfo}</span>
                    </div>
                    <div class='info-item-compact'>
                        <span class='info-label'>Uptime:</span>
                        <span class='info-value-small'>{uptimeString}</span>
                    </div>
                </div>
            </div>
            
            <div class='status-card'>
                <h3>📱 Cluster Applications</h3>
                <div class='info-value'>{applications.Count}</div>
                <div class='info-label'>Deployed applications</div>
                <div class='health-overview'>
                    {GetHealthSummary(applications.Select(a => a.HealthState))}
                </div>
            </div>
            
            <div class='status-card'>
                <h3>⚙️ Cluster Services</h3>
                <div class='info-value'>{services.Count}</div>
                <div class='info-label'>Active services</div>
                <div class='health-overview'>
                    {GetHealthSummary(services.Select(s => s.HealthState))}
                </div>
            </div>
        </div>

        <!-- Applications & Services Section -->
        <div class='applications-section'>
            <h2>📱 Cluster Applications & Services</h2>
            <div class='applications-list'>
                {string.Join("", applications.Select(app => $@"
                <div class='application-item'>
                    <div class='application-header'>
                        <div class='application-name'>{app.ApplicationName.ToString().Replace("fabric:/", "")}</div>
                        <div class='health-status {GetHealthCssClass(app.HealthState)}'>
                            <span class='status-indicator'></span>
                            {app.HealthState}
                        </div>
                    </div>
                    <div class='application-details'>
                        <span class='app-type'>{app.ApplicationTypeName} v{app.ApplicationTypeVersion}</span>
                    </div>
                    <div class='service-list'>
                        {string.Join("", services.Where(s => s.ServiceName.ToString().StartsWith(app.ApplicationName.ToString())).Select(service => $@"
                        <div class='service-item'>
                            <span class='service-name'>{service.ServiceName.ToString().Replace(app.ApplicationName.ToString() + "/", "")}</span>
                            <span class='service-type'>{service.ServiceKind} - {service.ServiceTypeName}</span>
                            <span class='health-status {GetHealthCssClass(service.HealthState)}'>
                                <span class='status-indicator'></span>
                                {service.HealthState}
                            </span>
                        </div>"))}
                    </div>
                </div>"))}
                {(applications.Count == 0 ? "<div class='no-items'>No applications found or limited access mode</div>" : "")}
            </div>
        </div>

        <!-- Nodes Section -->
        <div class='nodes-section'>
            <h2>🖥️ Cluster Nodes</h2>
            <div class='nodes-list'>
                {string.Join("", nodes.Select(node => $@"
                <div class='node-item'>
                    <div class='node-main'>
                        <div class='node-name copyable' onclick='copyToClipboard(""{node.NodeName}"")'>{node.NodeName}</div>
                        <div class='health-status {GetHealthCssClass(node.HealthState)}'>
                            <span class='status-indicator'></span>
                            {node.HealthState}
                        </div>
                    </div>
                    <div class='node-details-grid'>
                        <div class='node-detail'>
                            <div class='info-label'>IP Address or FQDN</div>
                            <div class='info-value copyable' onclick='copyToClipboard(""{node.IpAddressOrFQDN}"")'>{node.IpAddressOrFQDN}</div>
                        </div>
                        <div class='node-detail'>
                            <div class='info-label'>Status</div>
                            <div class='info-value node-status {node.NodeStatus.ToString().ToLower()}'>{node.NodeStatus}</div>
                        </div>
                        <div class='node-detail'>
                            <div class='info-label'>Fault Domain</div>
                            <div class='info-value'>{node.FaultDomain?.ToString() ?? "N/A"}</div>
                        </div>
                        <div class='node-detail'>
                            <div class='info-label'>Upgrade Domain</div>
                            <div class='info-value'>{node.UpgradeDomain?.ToString() ?? "N/A"}</div>
                        </div>
                    </div>
                </div>"))}
                {(nodes.Count == 0 ? "<div class='no-items'>No nodes found or limited access mode</div>" : "")}
            </div>
        </div>

        <div class='last-updated'>
            Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} • Auto-refreshes every 30 seconds
        </div>
    </div>

    <button class='refresh-button' onclick='refreshDashboard()'>
        🔄 Refresh
    </button>
</body>
</html>";

            return html;
        }

        private string GetOperatingSystemInfo()
        {
            try
            {
                // Try to get a more user-friendly OS name
                var osVersion = Environment.OSVersion;

                if (osVersion.Platform == PlatformID.Win32NT)
                {
                    var version = osVersion.Version;

                    // Windows version detection
                    if (version.Major == 10)
                    {
                        if (version.Build >= 20348)
                            return "Windows Server 2022";
                        else if (version.Build >= 17763)
                            return "Windows Server 2019";
                        else if (version.Build >= 14393)
                            return "Windows Server 2016";
                        else if (version.Build >= 22000)
                            return "Windows 11";
                        else
                            return "Windows 10";
                    }
                    else if (version.Major == 6)
                    {
                        if (version.Minor == 3)
                            return "Windows Server 2012 R2";
                        else if (version.Minor == 2)
                            return "Windows Server 2012";
                        else if (version.Minor == 1)
                            return "Windows Server 2008 R2";
                        else if (version.Minor == 0)
                            return "Windows Server 2008";
                    }
                }

                // Fallback to the original OS version string but cleaned up
                var osString = osVersion.ToString();
                return osString.Replace("Microsoft Windows NT ", "Windows ");
            }
            catch
            {
                return "Windows Server";
            }
        }

        private Dictionary<string, string> GetAdditionalTRPInfo()
        {
            var info = new Dictionary<string, string>();

            try
            {
                // Service Context Information
                info["Service Instance ID"] = _serviceContext.InstanceId.ToString();
                info["Partition ID"] = _serviceContext.PartitionId.ToString();
                info["Service Type"] = _serviceContext.ServiceTypeName;
                info["Code Package Version"] = _serviceContext.CodePackageActivationContext.CodePackageVersion;
                info["Application Name"] = _serviceContext.CodePackageActivationContext.ApplicationName;
                info["Application Type"] = _serviceContext.CodePackageActivationContext.ApplicationTypeName;

                // Environment Information
                info["Working Directory"] = Environment.CurrentDirectory;
                info["Process ID"] = Environment.ProcessId.ToString();
                info["User Domain"] = Environment.UserDomainName;
                info["User Name"] = Environment.UserName;
                info["64-bit Process"] = Environment.Is64BitProcess.ToString();
                info["64-bit OS"] = Environment.Is64BitOperatingSystem.ToString();

                // Network Information
                try
                {
                    var hostName = Dns.GetHostName();
                    info["Host Name"] = hostName;

                    var addresses = Dns.GetHostAddresses(hostName);
                    var ipv4Addresses = addresses.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    info["All IPv4 Addresses"] = string.Join(", ", ipv4Addresses.Select(ip => ip.ToString()));
                }
                catch (Exception ex)
                {
                    info["Network Info"] = $"Error retrieving: {ex.Message}";
                }

                // Time Zone Information
                info["Time Zone"] = TimeZoneInfo.Local.DisplayName;
                info["UTC Offset"] = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).ToString();

                // Service Fabric Specific
                info["Node Fault Domain"] = _serviceContext.NodeContext.NodeType;
                info["Node Type"] = _serviceContext.NodeContext.NodeType;

                // Dashboard Specific
                info["Dashboard Endpoint"] = "http://localhost:8081/health-dashboard";
                info["Health API Endpoint"] = "http://localhost:8081/health";
                info["Service Fabric Explorer"] = "http://localhost:19080";

            }
            catch (Exception ex)
            {
                info["Error"] = $"Failed to retrieve additional info: {ex.Message}";
            }

            return info;
        }

        private async Task<Node> GetCurrentNodeAsync()
        {
            try
            {
                if (_fabricClient == null) return null;

                var nodeList = await _fabricClient.QueryManager.GetNodeListAsync();
                var currentNodeName = _serviceContext.NodeContext.NodeName;
                var currentNode = nodeList.FirstOrDefault(n => n.NodeName == currentNodeName);

                if (currentNode != null)
                {
                    return currentNode;
                }

                // If we can't find the current node, return the first available node
                return nodeList.FirstOrDefault();
            }
            catch (Exception ex)
            {
                // If we can't get nodes from Fabric Client, return null and handle it in the calling code
                return null;
            }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName())
                    .AddressList
                    .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private async Task<List<Application>> GetApplicationsAsync()
        {
            try
            {
                if (_fabricClient == null) return new List<Application>();

                var applications = await _fabricClient.QueryManager.GetApplicationListAsync();
                return applications.ToList();
            }
            catch
            {
                return new List<Application>();
            }
        }

        private async Task<List<Service>> GetServicesAsync()
        {
            try
            {
                if (_fabricClient == null) return new List<Service>();

                var allServices = new List<Service>();

                // First get all applications
                var applications = await _fabricClient.QueryManager.GetApplicationListAsync();

                // Then get services for each application
                foreach (var app in applications)
                {
                    try
                    {
                        var services = await _fabricClient.QueryManager.GetServiceListAsync(app.ApplicationName);
                        allServices.AddRange(services);
                    }
                    catch
                    {
                        // Skip this application if we can't get its services
                        continue;
                    }
                }

                return allServices;
            }
            catch
            {
                return new List<Service>();
            }
        }

        private async Task<List<Node>> GetNodesAsync()
        {
            try
            {
                if (_fabricClient == null) return new List<Node>();

                var nodes = await _fabricClient.QueryManager.GetNodeListAsync();
                return nodes.ToList();
            }
            catch
            {
                return new List<Node>();
            }
        }

        private async Task<string> GetServiceFabricVersionAsync()
        {
            try
            {
                if (_fabricClient == null) return "Unknown (Limited Mode)";

                var clusterHealth = await _fabricClient.HealthManager.GetClusterHealthAsync();
                return "11.1.208"; // Your specified runtime version
            }
            catch
            {
                return "Unknown";
            }
        }

        private string GetDotNetVersion()
        {
            try
            {
                return RuntimeInformation.FrameworkDescription;
            }
            catch
            {
                return "Not Installed";
            }
        }

        private HardwareInfo GetHardwareInfo()
        {
            try
            {
                var info = new HardwareInfo();

                // Get CPU cores
                info.CpuCores = Environment.ProcessorCount;

                // Get memory information - simplified approach using GC
                try
                {
                    var gcMemoryInfo = GC.GetGCMemoryInfo();
                    if (gcMemoryInfo.TotalAvailableMemoryBytes > 0)
                    {
                        info.TotalMemoryGB = gcMemoryInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
                        info.AvailableMemoryGB = info.TotalMemoryGB * 0.7; // Estimate available as 70% of total
                    }
                    else
                    {
                        // Fallback estimation
                        info.TotalMemoryGB = 8.0; // Default assumption
                        info.AvailableMemoryGB = 5.6;
                    }
                }
                catch
                {
                    // Fallback values
                    info.TotalMemoryGB = 8.0;
                    info.AvailableMemoryGB = 5.6;
                }

                return info;
            }
            catch
            {
                return new HardwareInfo { CpuCores = Environment.ProcessorCount, TotalMemoryGB = 8.0, AvailableMemoryGB = 5.6 };
            }
        }

        private string GetHealthCssClass(HealthState healthState)
        {
            return healthState switch
            {
                HealthState.Ok => "health-ok",
                HealthState.Warning => "health-warning",
                HealthState.Error => "health-error",
                _ => "health-unknown"
            };
        }

        private string GetHealthSummary(IEnumerable<HealthState> healthStates)
        {
            var states = healthStates.ToList();
            if (!states.Any()) return "<span class='health-summary'>No items</span>";

            var okCount = states.Count(h => h == HealthState.Ok);
            var warningCount = states.Count(h => h == HealthState.Warning);
            var errorCount = states.Count(h => h == HealthState.Error);
            var unknownCount = states.Count(h => h == HealthState.Unknown);

            var summary = new List<string>();
            if (okCount > 0) summary.Add($"<span class='health-summary ok'>{okCount} OK</span>");
            if (warningCount > 0) summary.Add($"<span class='health-summary warning'>{warningCount} Warning</span>");
            if (errorCount > 0) summary.Add($"<span class='health-summary error'>{errorCount} Error</span>");
            if (unknownCount > 0) summary.Add($"<span class='health-summary unknown'>{unknownCount} Unknown</span>");

            return string.Join(" ", summary);
        }

        private string GetDashboardStyles()
        {
            return @"
:root {
    --primary-blue: #003f7f;
    --primary-light-blue: #0066cc;
    --primary-green: #00a651;
    --primary-orange: #ff6900;
    --primary-red: #e31e24;
    --primary-gray: #6c757d;
    --primary-light-gray: #f8f9fa;
    --primary-white: #ffffff;
    --primary-dark: #212529;
}

* {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
}

body {
    font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
    background: linear-gradient(135deg, var(--primary-light-gray) 0%, #e8f4f8 100%);
    color: var(--primary-dark);
    line-height: 1.6;
    min-height: 100vh;
}

.header {
    background: linear-gradient(135deg, var(--primary-blue) 0%, var(--primary-light-blue) 100%);
    color: var(--primary-white);
    padding: 2rem 0;
    text-align: center;
    box-shadow: 0 4px 12px rgba(0, 63, 127, 0.2);
}

.header-content {
    max-width: 1400px;
    margin: 0 auto;
    padding: 0 2rem;
}

.department-name {
    font-size: 1.8rem;
    font-weight: 600;
    margin-bottom: 0.5rem;
}

.cluster-name {
    font-size: 1.3rem;
    font-weight: 400;
    opacity: 0.9;
}

.container {
    max-width: 1400px;
    margin: 0 auto;
    padding: 2rem;
}

.status-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
    gap: 1.5rem;
    margin-bottom: 2rem;
}

.status-card {
    background: var(--primary-white);
    border-radius: 12px;
    padding: 1.5rem;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
    border-left: 4px solid var(--primary-blue);
    transition: transform 0.2s ease, box-shadow 0.2s ease;
}

.status-card:hover {
    transform: translateY(-2px);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.15);
}

.status-card h3 {
    color: var(--primary-blue);
    margin-bottom: 1rem;
    font-size: 1.2rem;
    font-weight: 600;
}

.health-status {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 1rem;
    border-radius: 20px;
    font-weight: 500;
    font-size: 0.9rem;
}

.health-ok {
    background: rgba(0, 166, 81, 0.1);
    color: var(--primary-green);
    border: 1px solid var(--primary-green);
}

.health-warning {
    background: rgba(255, 105, 0, 0.1);
    color: var(--primary-orange);
    border: 1px solid var(--primary-orange);
}

.health-error {
    background: rgba(227, 30, 36, 0.1);
    color: var(--primary-red);
    border: 1px solid var(--primary-red);
}

.health-unknown {
    background: rgba(108, 117, 125, 0.1);
    color: var(--primary-gray);
    border: 1px solid var(--primary-gray);
}

.info-value {
    font-size: 1.8rem;
    font-weight: 700;
    color: var(--primary-dark);
    margin-bottom: 0.5rem;
}

.info-value-small {
    font-size: 0.95rem;
    font-weight: 500;
    color: var(--primary-dark);
    word-break: break-all;
}

.info-label {
    font-size: 0.9rem;
    color: var(--primary-gray);
    margin-bottom: 0.3rem;
}

.node-details {
    margin-top: 1rem;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
    gap: 0.5rem;
}

.detail-item {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
}

.detail-label {
    font-size: 0.8rem;
    color: var(--primary-gray);
}

.detail-value {
    font-size: 0.9rem;
    font-weight: 500;
    color: var(--primary-dark);
}

.info-grid-compact {
    display: grid;
    grid-template-columns: 1fr;
    gap: 0.5rem;
}

.info-item-compact {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.3rem 0;
    border-bottom: 1px solid var(--primary-light-gray);
}

.info-item-compact:last-child {
    border-bottom: none;
}

.health-overview {
    margin-top: 0.5rem;
}

.health-summary {
    display: inline-block;
    padding: 0.2rem 0.6rem;
    border-radius: 12px;
    font-size: 0.8rem;
    font-weight: 500;
    margin-right: 0.3rem;
}

.health-summary.ok {
    background: rgba(0, 166, 81, 0.1);
    color: var(--primary-green);
}

.health-summary.warning {
    background: rgba(255, 105, 0, 0.1);
    color: var(--primary-orange);
}

.health-summary.error {
    background: rgba(227, 30, 36, 0.1);
    color: var(--primary-red);
}

.health-summary.unknown {
    background: rgba(108, 117, 125, 0.1);
    color: var(--primary-gray);
}

.applications-section, .nodes-section {
    background: var(--primary-white);
    border-radius: 12px;
    padding: 2rem;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
    margin-bottom: 2rem;
}

.applications-section h2, .nodes-section h2 {
    color: var(--primary-blue);
    margin-bottom: 1.5rem;
    font-size: 1.5rem;
    font-weight: 600;
}

.application-item {
    background: var(--primary-light-gray);
    border-radius: 8px;
    padding: 1.5rem;
    margin-bottom: 1rem;
    border-left: 4px solid var(--primary-light-blue);
}

.application-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 0.5rem;
}

.application-name {
    font-size: 1.2rem;
    font-weight: 600;
    color: var(--primary-blue);
}

.application-details {
    margin-bottom: 1rem;
}

.app-type {
    font-size: 0.9rem;
    color: var(--primary-gray);
}

.service-list {
    margin-left: 1rem;
}

.service-item {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 1rem;
    align-items: center;
    padding: 0.8rem 0;
    border-bottom: 1px solid rgba(0, 0, 0, 0.1);
}

.service-item:last-child {
    border-bottom: none;
}

.service-name {
    font-weight: 500;
    color: var(--primary-dark);
}

0.85rem;
    color: var(--primary-gray);
}

.node-item {
    background: var(--primary-light-gray);
    border-radius: 8px;
    padding: 1.5rem;
    margin-bottom: 1rem;
}

.node-main {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
}

.node-name {
    font-weight: 600;
    color: var(--primary-blue);
    font-size: 1.1rem;
}

.node-details-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
    gap: 1rem;
}

.node-detail {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
}

.node-status.up {
    color: var(--primary-green);
    font-weight: 600;
}

.node-status.down {
    color: var(--primary-red);
    font-weight: 600;
}

.refresh-button {
    position: fixed;
    bottom: 2rem;
    right: 2rem;
    background: var(--primary-blue);
    color: var(--primary-white);
    border: none;
    border-radius: 50px;
    padding: 1rem 1.5rem;
    font-size: 1rem;
    font-weight: 600;
    cursor: pointer;
    box-shadow: 0 4px 12px rgba(0, 63, 127, 0.3);
    transition: all 0.2s ease;
}

.refresh-button:hover {
    background: var(--primary-light-blue);
    transform: translateY(-2px);
    box-shadow: 0 6px 16px rgba(0, 63, 127, 0.4);
}

.status-indicator {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    display: inline-block;
    margin-right: 0.5rem;
}

.health-ok .status-indicator {
    background: var(--primary-green);
}

.health-warning .status-indicator {
    background: var(--primary-orange);
}

.health-error .status-indicator {
    background: var(--primary-red);
}

.health-unknown .status-indicator {
    background: var(--primary-gray);
}

.last-updated {
    text-align: center;
    color: var(--primary-gray);
    font-size: 0.9rem;
    margin-top: 2rem;
    padding: 1rem;
    background: var(--primary-white);
    border-radius: 8px;
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);
}

.copyable {
    cursor: pointer;
    padding: 0.2rem 0.4rem;
    border-radius: 4px;
    transition: background-color 0.2s ease;
}

.copyable:hover {
    background-color: rgba(0, 102, 204, 0.1);
    color: var(--primary-blue);
}

.no-items {
    padding: 2rem;
    text-align: center;
    color: var(--primary-gray);
    font-style: italic;
    background: var(--primary-light-gray);
    border-radius: 8px;
}

@media (max-width: 768px) {
    .container {
        padding: 1rem;
    }
    
    .status-grid {
        grid-template-columns: 1fr;
    }
    
    .node-details-grid {
        grid-template-columns: 1fr;
    }
    
    .application-header {
        flex-direction: column;
        align-items: flex-start;
        gap: 0.5rem;
    }
    
    .service-item {
        grid-template-columns: 1fr;
        gap: 0.5rem;
    }
    
    .node-main {
        flex-direction: column;
        align-items: flex-start;
        gap: 0.5rem;
    }
    
    .refresh-button {
        bottom: 1rem;
        right: 1rem;
        padding: 0.8rem 1.2rem;
    }
}

@media (max-width: 480px) {
    .department-name {
        font-size: 1.4rem;
    }
    
    .cluster-name {
        font-size: 1.1rem;
    }
    
    .info-value {
        font-size: 1.4rem;
    }
}";
        }
    }

    public class HardwareInfo
    {
        public int CpuCores { get; set; }
        public double TotalMemoryGB { get; set; }
        public double AvailableMemoryGB { get; set; }
    }
}