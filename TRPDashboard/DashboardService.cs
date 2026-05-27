﻿using System;
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
using System.Text.Json;
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

            var scripts = $@"
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

        function downloadExport() {{
            const f = document.getElementById('exportFormat').value;
            window.location.href = 'export?format=' + encodeURIComponent(f);
        }}
    </script>";

            var body = $@"
    <div class='container'>
        <div class='page-head'>
            <div class='page-head-titles'>
                <h1>Cluster Overview</h1>
                <div class='sub'>Coordinating from node <strong>{H(currentNodeName)}</strong></div>
            </div>
            <div class='export-panel'>
                <label class='export-fmt'>Format
                    <select id='exportFormat'>
                        <option value='csv'>CSV</option>
                        <option value='json'>JSON</option>
                        <option value='txt'>Text (.txt)</option>
                    </select>
                </label>
                <button class='btn' onclick='downloadExport()'>Download</button>
            </div>
        </div>
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
        &#10227; Refresh
    </button>";

            return HealthUi.Layout("Service Fabric Health Dashboard", body, scripts);
        }

        // Builds a downloadable cluster-health snapshot (applications, services, nodes) in the
        // requested format. Returns the suggested filename, MIME type, and body for the response.
        public async Task<(string FileName, string ContentType, string Body)> GenerateExportAsync(string format)
        {
            var apps = await GetApplicationsAsync(FabricQueryTimeout);
            var services = await GetServicesAsync(apps, FabricQueryTimeout);
            var nodes = await GetNodesAsync(FabricQueryTimeout);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            switch ((format ?? "csv").ToLowerInvariant())
            {
                case "json":
                    return ($"cluster-health-{stamp}.json", "application/json", ExportJson(apps, services, nodes));
                case "txt":
                    return ($"cluster-health-{stamp}.txt", "text/plain; charset=utf-8", ExportTxt(apps, services, nodes));
                default:
                    return ($"cluster-health-{stamp}.csv", "text/csv; charset=utf-8", ExportCsv(apps, services, nodes));
            }
        }

        private static string ExportJson(List<Application> apps, List<Service> services, List<Node> nodes)
        {
            var payload = new
            {
                generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                applications = apps.Select(a => new {
                    name = a.ApplicationName?.ToString(),
                    typeName = a.ApplicationTypeName,
                    typeVersion = a.ApplicationTypeVersion,
                    healthState = a.HealthState.ToString()
                }),
                services = services.Select(s => new {
                    name = s.ServiceName?.ToString(),
                    typeName = s.ServiceTypeName,
                    kind = s.ServiceKind.ToString(),
                    healthState = s.HealthState.ToString()
                }),
                nodes = nodes.Select(n => new {
                    name = n.NodeName,
                    ipAddressOrFQDN = n.IpAddressOrFQDN,
                    status = n.NodeStatus.ToString(),
                    healthState = n.HealthState.ToString(),
                    faultDomain = n.FaultDomain?.ToString(),
                    upgradeDomain = n.UpgradeDomain
                })
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string ExportCsv(List<Application> apps, List<Service> services, List<Node> nodes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Applications");
            sb.AppendLine("Name,TypeName,TypeVersion,HealthState");
            foreach (var a in apps)
                sb.AppendLine(string.Join(",", Csv(a.ApplicationName?.ToString()), Csv(a.ApplicationTypeName), Csv(a.ApplicationTypeVersion), Csv(a.HealthState.ToString())));
            sb.AppendLine();
            sb.AppendLine("Services");
            sb.AppendLine("Name,TypeName,Kind,HealthState");
            foreach (var s in services)
                sb.AppendLine(string.Join(",", Csv(s.ServiceName?.ToString()), Csv(s.ServiceTypeName), Csv(s.ServiceKind.ToString()), Csv(s.HealthState.ToString())));
            sb.AppendLine();
            sb.AppendLine("Nodes");
            sb.AppendLine("Name,IpAddressOrFQDN,Status,HealthState,FaultDomain,UpgradeDomain");
            foreach (var n in nodes)
                sb.AppendLine(string.Join(",", Csv(n.NodeName), Csv(n.IpAddressOrFQDN), Csv(n.NodeStatus.ToString()), Csv(n.HealthState.ToString()), Csv(n.FaultDomain?.ToString()), Csv(n.UpgradeDomain)));
            return sb.ToString();
        }

        // Quote a CSV field if it contains a comma, quote, or newline; double embedded quotes.
        private static string Csv(string v)
        {
            v ??= "";
            return (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v;
        }

        private static string ExportTxt(List<Application> apps, List<Service> services, List<Node> nodes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Service Fabric Cluster Health");
            sb.AppendLine("Generated (UTC): " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine($"APPLICATIONS ({apps.Count})");
            foreach (var a in apps)
                sb.AppendLine($"  {a.ApplicationName?.ToString()?.Replace("fabric:/", "")}  [{a.HealthState}]  {a.ApplicationTypeName} v{a.ApplicationTypeVersion}");
            sb.AppendLine();
            sb.AppendLine($"SERVICES ({services.Count})");
            foreach (var s in services)
                sb.AppendLine($"  {s.ServiceName?.ToString()?.Replace("fabric:/", "")}  [{s.HealthState}]  {s.ServiceKind}/{s.ServiceTypeName}");
            sb.AppendLine();
            sb.AppendLine($"NODES ({nodes.Count})");
            foreach (var n in nodes)
                sb.AppendLine($"  {n.NodeName}  [{n.HealthState}]  {n.NodeStatus}  {n.IpAddressOrFQDN}  FD={n.FaultDomain} UD={n.UpgradeDomain}");
            return sb.ToString();
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
            // 1) %FabricCodePath% (set by a full runtime install) -> FabricCommon.dll file version.
            try
            {
                var fabricCodePath = Environment.GetEnvironmentVariable("FabricCodePath");
                var v = VersionFromCodePath(fabricCodePath);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }

            // 2) Registry fallback. SDK dev clusters often don't set %FabricCodePath%, but the
            //    runtime still records its version + code path under HKLM. FabricVersion is
            //    authoritative; otherwise read FabricCommon.dll under the registry's FabricCodePath.
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Service Fabric"))
                {
                    if (key != null)
                    {
                        if (key.GetValue("FabricVersion") is string ver && !string.IsNullOrEmpty(ver)) return ver;
                        var v = VersionFromCodePath(key.GetValue("FabricCodePath") as string);
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch { }

            return "Unknown";
        }

        // FabricCommon.dll's file version tracks the node's installed runtime version.
        private static string VersionFromCodePath(string fabricCodePath)
        {
            if (string.IsNullOrEmpty(fabricCodePath)) return null;
            var dllPath = Path.Combine(fabricCodePath, "FabricCommon.dll");
            return File.Exists(dllPath) ? FileVersionInfo.GetVersionInfo(dllPath).FileVersion : null;
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