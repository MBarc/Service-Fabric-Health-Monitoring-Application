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
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TRPDashboard
{
    public class DashboardService
    {
        // Configuration - Department name that can be easily changed.
        // Public so the home page (TRPDashboard.cs) can render the same value.
        public const string DEPARTMENT_NAME = "Internal Department Name";

        private readonly FabricClient _fabricClient;
        private readonly StatelessServiceContext _serviceContext;

        public DashboardService(FabricClient fabricClient, StatelessServiceContext serviceContext)
        {
            _fabricClient = fabricClient;
            _serviceContext = serviceContext;
        }

        // Bound every Fabric query so a slow cluster manager cannot hang the HTTP listener.
        private static readonly TimeSpan FabricQueryTimeout = TimeSpan.FromSeconds(3);

        // Values that cannot change without restarting this process. Computed once per process,
        // shared across every request (a new DashboardService is constructed per request).
        // Host-installed .NET runtimes are deliberately NOT memoized — an admin can install one
        // while the dashboard is running, and the operator wants to see that reflected.
        private static readonly Lazy<string> CachedServiceFabricVersion = new Lazy<string>(ReadServiceFabricVersion);
        private static readonly Lazy<string> CachedOperatingSystemInfo = new Lazy<string>(ReadOperatingSystemInfo);
        private static readonly Lazy<string> CachedLocalIPAddress = new Lazy<string>(ReadLocalIPAddress);

        public async Task<string> GenerateDashboardHtml()
        {
            // Kick off the two independent cluster-wide queries in parallel.
            var nodesTask = GetNodesAsync(FabricQueryTimeout);
            var applicationsTask = GetApplicationsAsync(FabricQueryTimeout);
            await Task.WhenAll(nodesTask, applicationsTask);
            var nodes = nodesTask.Result;
            var applications = applicationsTask.Result;

            // Fan out one service query per app, all in parallel.
            var services = await GetServicesAsync(applications, FabricQueryTimeout);

            // Local-only metadata: pure CPU / static file reads, no need to await.
            var serviceFabricVersion = GetServiceFabricVersion();
            var dotNetVersion = GetDotNetVersion();
            var dotNetFrameworkVersion = GetDotNetFrameworkVersion();
            var hardwareInfo = GetHardwareInfo();
            var osInfo = GetOperatingSystemInfo();

            // Derive the current node from the already-fetched list (no extra round-trip).
            var contextNodeName = _serviceContext.NodeContext.NodeName;
            var currentNode = nodes.FirstOrDefault(n => n.NodeName == contextNodeName) ?? nodes.FirstOrDefault();
            var currentNodeName = currentNode?.NodeName ?? Environment.MachineName;
            var currentNodeIp = GetLocalIPAddress();
            var currentNodeHostname = Environment.MachineName;

            // System uptime since boot. TickCount64 avoids the ~49-day int overflow of TickCount.
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
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
        // Fetch the dashboard HTML and swap just the .container into place so scroll
        // position, focus, and any in-flight toast survive the refresh.
        async function refreshContent() {{
            try {{
                const res = await fetch(location.pathname, {{ cache: 'no-store' }});
                if (!res.ok) return;
                const html = await res.text();
                const parsed = new DOMParser().parseFromString(html, 'text/html');
                const fresh = parsed.querySelector('.container');
                const current = document.querySelector('.container');
                if (fresh && current) current.replaceWith(fresh);
            }} catch (e) {{
                // Swallow transient failures; the next tick will try again.
            }}
        }}

        // Auto-refresh every 30 seconds.
        setInterval(refreshContent, 30000);

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
            setTimeout(() => {{ button.style.transform = ''; refreshContent(); }}, 500);
        }}
    </script>
</head>
<body>
    <header class='header'>
        <div class='header-content'>
            <div class='department-name'>{H(DEPARTMENT_NAME)}</div>
            <div class='cluster-name'>Service Fabric Cluster</div>
        </div>
    </header>

    <div class='container'>
        <!-- Status Overview Cards -->
        <div class='status-grid'>
            <div class='status-card'>
                <h3>🖥️ Current Node</h3>
                <div class='info-value copyable' onclick='copyToClipboard(""{J(currentNodeName)}"")'>{H(currentNodeName)}</div>
                <div class='info-label'>Handling this request</div>
                <div class='node-details'>
                    <div class='detail-item'>
                        <span class='detail-label'>IP Address:</span>
                        <span class='detail-value copyable' onclick='copyToClipboard(""{J(currentNodeIp)}"")'>{H(currentNodeIp)}</span>
                    </div>
                    <div class='detail-item'>
                        <span class='detail-label'>Hostname:</span>
                        <span class='detail-value copyable' onclick='copyToClipboard(""{J(currentNodeHostname)}"")'>{H(currentNodeHostname)}</span>
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
                        <span class='info-value-small'>{H(serviceFabricVersion)}</span>
                    </div>
                    <div class='info-item-compact'>
                        <span class='info-label'>.NET Runtimes:</span>
                        <span class='info-value-small'>{H(dotNetVersion)}</span>
                    </div>
                    <div class='info-item-compact'>
                        <span class='info-label'>.NET Framework:</span>
                        <span class='info-value-small'>{H(dotNetFrameworkVersion)}</span>
                    </div>
                    <div class='info-item-compact'>
                        <span class='info-label'>OS:</span>
                        <span class='info-value-small'>{H(osInfo)}</span>
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
                        <div class='application-name'>{H(app.ApplicationName.ToString().Replace("fabric:/", ""))}</div>
                        <div class='health-status {GetHealthCssClass(app.HealthState)}'>
                            <span class='status-indicator'></span>
                            {app.HealthState}
                        </div>
                    </div>
                    <div class='application-details'>
                        <span class='app-type'>{H(app.ApplicationTypeName)} v{H(app.ApplicationTypeVersion)}</span>
                    </div>
                    <div class='service-list'>
                        {string.Join("", services.Where(s => s.ServiceName.ToString().StartsWith(app.ApplicationName.ToString())).Select(service => $@"
                        <div class='service-item'>
                            <span class='service-name'>{H(service.ServiceName.ToString().Replace(app.ApplicationName.ToString() + "/", ""))}</span>
                            <span class='service-type'>{service.ServiceKind} - {H(service.ServiceTypeName)}</span>
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
                        <div class='node-name copyable' onclick='copyToClipboard(""{J(node.NodeName)}"")'>{H(node.NodeName)}</div>
                        <div class='health-status {GetHealthCssClass(node.HealthState)}'>
                            <span class='status-indicator'></span>
                            {node.HealthState}
                        </div>
                    </div>
                    <div class='node-details-grid'>
                        <div class='node-detail'>
                            <div class='info-label'>IP Address or FQDN</div>
                            <div class='info-value copyable' onclick='copyToClipboard(""{J(node.IpAddressOrFQDN)}"")'>{H(node.IpAddressOrFQDN)}</div>
                        </div>
                        <div class='node-detail'>
                            <div class='info-label'>Status</div>
                            <div class='info-value node-status {node.NodeStatus.ToString().ToLower()}'>{node.NodeStatus}</div>
                        </div>
                        <div class='node-detail'>
                            <div class='info-label'>Fault Domain</div>
                            <div class='info-value'>{H(node.FaultDomain?.ToString() ?? "N/A")}</div>
                        </div>
                        <div class='node-detail'>
                            <div class='info-label'>Upgrade Domain</div>
                            <div class='info-value'>{H(node.UpgradeDomain ?? "N/A")}</div>
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

        private string GetOperatingSystemInfo() => CachedOperatingSystemInfo.Value;

        // Reads the OS marketing name Microsoft maintains per release at
        // HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion. ProductName +
        // DisplayVersion together carry strings like "Windows Server 2022 Datacenter 22H2",
        // updated by Microsoft for every new SKU — no thresholds to maintain on our side.
        private static string ReadOperatingSystemInfo()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return "Windows (unknown)";

                var productName = key.GetValue("ProductName") as string;
                if (string.IsNullOrEmpty(productName)) return "Windows (unknown)";

                // Microsoft never updated ProductName on Windows 11 client builds (>= 22000);
                // it still reads "Windows 10 <edition>". Patch when we can confirm the build.
                // Server SKUs report correctly, so this only kicks in on workstation installs.
                if (productName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(key.GetValue("CurrentBuildNumber") as string, out var build) &&
                    build >= 22000)
                {
                    productName = "Windows 11" + productName.Substring("Windows 10".Length);
                }

                var displayVersion = key.GetValue("DisplayVersion") as string;
                return string.IsNullOrEmpty(displayVersion) ? productName : $"{productName} {displayVersion}";
            }
            catch
            {
                return "Windows (unknown)";
            }
        }

        private string GetLocalIPAddress() => CachedLocalIPAddress.Value;

        private static string ReadLocalIPAddress()
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

        private async Task<List<Application>> GetApplicationsAsync(TimeSpan timeout)
        {
            try
            {
                if (_fabricClient == null) return new List<Application>();

                var applications = await _fabricClient.QueryManager.GetApplicationListAsync(null, timeout, CancellationToken.None);
                return applications.ToList();
            }
            catch
            {
                return new List<Application>();
            }
        }

        private async Task<List<Service>> GetServicesAsync(IReadOnlyCollection<Application> applications, TimeSpan timeout)
        {
            if (_fabricClient == null || applications == null || applications.Count == 0)
            {
                return new List<Service>();
            }

            // Run all per-app service queries concurrently. One slow app cannot delay the others.
            var tasks = applications.Select(app => GetServicesForAppAsync(app.ApplicationName, timeout)).ToList();
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(r => r).ToList();
        }

        private async Task<List<Service>> GetServicesForAppAsync(Uri applicationName, TimeSpan timeout)
        {
            try
            {
                var services = await _fabricClient.QueryManager.GetServiceListAsync(applicationName, null, timeout, CancellationToken.None);
                return services.ToList();
            }
            catch
            {
                return new List<Service>();
            }
        }

        private async Task<List<Node>> GetNodesAsync(TimeSpan timeout)
        {
            try
            {
                if (_fabricClient == null) return new List<Node>();

                var nodes = await _fabricClient.QueryManager.GetNodeListAsync(null, timeout, CancellationToken.None);
                return nodes.ToList();
            }
            catch
            {
                return new List<Node>();
            }
        }

        private string GetServiceFabricVersion() => CachedServiceFabricVersion.Value;

        private static string ReadServiceFabricVersion()
        {
            try
            {
                // The SF runtime sets %FabricCodePath% to the runtime install directory.
                // FabricCommon.dll's file version tracks the actual runtime version on the node.
                var fabricCodePath = Environment.GetEnvironmentVariable("FabricCodePath");
                if (string.IsNullOrEmpty(fabricCodePath)) return "Unknown";

                var dllPath = Path.Combine(fabricCodePath, "FabricCommon.dll");
                if (!File.Exists(dllPath)) return "Unknown";

                return FileVersionInfo.GetVersionInfo(dllPath).FileVersion ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private string GetDotNetVersion() => ReadHostDotNetRuntimes();

        // Reports the .NET runtimes installed ON THE HOST, not the runtime bundled inside this
        // self-contained service. RuntimeInformation.FrameworkDescription would only return the
        // bundled version, which is frozen at build time and tells the operator nothing about
        // the server they are looking at.
        private static string ReadHostDotNetRuntimes()
        {
            try
            {
                var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                if (string.IsNullOrEmpty(dotnetRoot))
                {
                    dotnetRoot = @"C:\Program Files\dotnet";
                }

                var runtimeDir = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(runtimeDir))
                {
                    return "Not Installed";
                }

                var versions = Directory.GetDirectories(runtimeDir)
                    .Select(Path.GetFileName)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                    .ToList();

                return versions.Count == 0 ? "Not Installed" : string.Join(", ", versions);
            }
            catch
            {
                return "Not Installed";
            }
        }

        private string GetDotNetFrameworkVersion() => ReadHostDotNetFramework();

        // Reads the .NET Framework 4.x version installed on the host via the Release DWORD
        // documented at https://learn.microsoft.com/dotnet/framework/migration-guide/how-to-determine-which-versions-are-installed.
        // Server 2022 ships with 4.8 (528040+) or 4.8.1 (533320+) after recent updates.
        private static string ReadHostDotNetFramework()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var ndpKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                if (ndpKey?.GetValue("Release") is not int release)
                {
                    return "Not Installed";
                }

                if (release >= 533320) return "4.8.1";
                if (release >= 528040) return "4.8";
                if (release >= 461808) return "4.7.2";
                if (release >= 461308) return "4.7.1";
                if (release >= 460798) return "4.7";
                if (release >= 394802) return "4.6.2";
                if (release >= 394254) return "4.6.1";
                if (release >= 393295) return "4.6";
                if (release >= 379893) return "4.5.2";
                if (release >= 378675) return "4.5.1";
                if (release >= 378389) return "4.5";
                return $"4.x (Release {release})";
            }
            catch
            {
                return "Not Installed";
            }
        }

        private HardwareInfo GetHardwareInfo()
        {
            var info = new HardwareInfo { CpuCores = Environment.ProcessorCount };

            try
            {
                var mem = new NativeMethods.MEMORYSTATUSEX();
                if (NativeMethods.GlobalMemoryStatusEx(mem))
                {
                    const double BytesPerGB = 1024.0 * 1024.0 * 1024.0;
                    info.TotalMemoryGB = mem.ullTotalPhys / BytesPerGB;
                    info.AvailableMemoryGB = mem.ullAvailPhys / BytesPerGB;
                }
            }
            catch
            {
                // Leave memory fields at 0 on failure; the UI shows the raw value.
            }

            return info;
        }

        // HTML-encode arbitrary text for inclusion in element content / attribute values.
        private static string H(object value) => WebUtility.HtmlEncode(value?.ToString() ?? string.Empty);

        // Encode arbitrary text safely inside a JavaScript string literal in an HTML attribute.
        // JavaScriptEncoder produces valid JS; HtmlEncode then makes the encoded string safe
        // for the surrounding HTML attribute (e.g. onclick='copyToClipboard("…")').
        private static string J(object value)
            => WebUtility.HtmlEncode(JavaScriptEncoder.Default.Encode(value?.ToString() ?? string.Empty));

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

.service-type {
    font-size: 0.85rem;
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

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}