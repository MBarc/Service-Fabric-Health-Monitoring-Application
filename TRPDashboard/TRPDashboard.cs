using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace TRPDashboard
{
    internal sealed class TRPDashboard : StatelessService
    {
        public TRPDashboard(StatelessServiceContext context)
            : base(context)
        {
        }

        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new ServiceInstanceListener[]
            {
                new ServiceInstanceListener(
                    serviceContext => new SimpleHttpCommunicationListener(serviceContext),
                    "ServiceEndpoint")
            };
        }
    }

    public class SimpleHttpCommunicationListener : ICommunicationListener
    {
        private readonly StatelessServiceContext _serviceContext;
        private HttpListener _httpListener;
        private string _publishAddress;
        private CancellationTokenSource _cancellationTokenSource;

        public SimpleHttpCommunicationListener(StatelessServiceContext serviceContext)
        {
            _serviceContext = serviceContext;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        // Scheme the listener binds and the published address uses, taken from the SF endpoint
        // declaration. Https unless the package was deployed unsecured (Endpoint Protocol="http").
        private string ResolveScheme()
        {
            try
            {
                var ep = _serviceContext.CodePackageActivationContext.GetEndpoint("ServiceEndpoint");
                return ep.Protocol == EndpointProtocol.Https ? "https" : "http";
            }
            catch { return "https"; }
        }

        public Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                ServiceEventSource.Current.Message("=== SimpleHttpCommunicationListener starting ===");

                // Resolve the cluster name once for the browser-tab title (best-effort).
                try
                {
                    FabricClient fc = null;
                    try { fc = new FabricClient(); } catch { }
                    HealthUi.ClusterName = HealthUi.ResolveClusterName(fc, _serviceContext);
                }
                catch { }

                // URL prefix the browser reaches us under. Behind the SF reverse proxy this is the
                // service's Fabric path (fabric:/HealthMonitoring/TRPDashboard -> "/HealthMonitoring/
                // TRPDashboard"); emitted links carry it so they round-trip through the proxy. Direct
                // access keeps it empty.
                HealthUi.PathBase = HealthUi.ResolvePathBase(_serviceContext);

                // Scheme + port come straight from the SF endpoint declaration. Https normally;
                // http when the package was deployed unsecured (Build-And-Deploy.ps1 -Unsecured flips
                // the endpoint Protocol to http and strips the cert binding). Default port 8472 is the
                // dashboard's dedicated port - chosen, like the sibling apps' 8470/8444, so it doesn't
                // collide with another node service's http.sys binding on a shared node.
                string scheme = ResolveScheme();
                int port = 8472;

                try
                {
                    var endpoint = _serviceContext.CodePackageActivationContext.GetEndpoint("ServiceEndpoint");
                    port = endpoint.Port;
                    ServiceEventSource.Current.Message($"Got port from endpoint: {port}");
                }
                catch (Exception ex)
                {
                    ServiceEventSource.Current.Message($"Could not get endpoint port, using default 8472: {ex.Message}");
                    port = 8472;
                }

                // For an HTTPS endpoint, EndpointBindingPolicy in ApplicationManifest.xml binds the TLS
                // cert to this port via http.sys and HttpListener picks the binding up automatically.
                // For an unsecured (http) endpoint there is no binding to pick up - plain HTTP.
                var listeningAddress = $"{scheme}://+:{port}/";
                _publishAddress = $"{scheme}://localhost:{port}/";

                ServiceEventSource.Current.Message($"Attempting to start HttpListener on: {listeningAddress}");
                ServiceEventSource.Current.Message($"Publish address will be: {_publishAddress}");

                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add(listeningAddress);

                ServiceEventSource.Current.Message("Calling _httpListener.Start()...");
                _httpListener.Start();
                ServiceEventSource.Current.Message("HttpListener.Start() completed successfully!");

                ServiceEventSource.Current.HttpListenerStarted(_publishAddress);

                // Start processing requests
                ServiceEventSource.Current.Message("Starting request processing...");
                _ = Task.Run(async () => await ProcessRequestsAsync(_cancellationTokenSource.Token));

                ServiceEventSource.Current.Message($"=== HTTP Listener fully started on {_publishAddress} ===");
                return Task.FromResult(_publishAddress);
            }
            catch (Exception ex)
            {
                var errorMessage = $"CRITICAL ERROR in OpenAsync: {ex.GetType().Name} - {ex.Message}";
                ServiceEventSource.Current.HttpListenerError(errorMessage);
                ServiceEventSource.Current.Message($"Exception details: {ex}");
                throw;
            }
        }

        private async Task ProcessRequestsAsync(CancellationToken cancellationToken)
        {
            ServiceEventSource.Current.Message("ProcessRequestsAsync started");

            while (_httpListener != null && _httpListener.IsListening && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ServiceEventSource.Current.Message("Waiting for HTTP request...");
                    var context = await _httpListener.GetContextAsync();
                    ServiceEventSource.Current.Message($"Received request: {context.Request.Url}");

                    _ = Task.Run(() => HandleRequest(context), cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    ServiceEventSource.Current.Message("HttpListener disposed - stopping request processing");
                    break;
                }
                catch (HttpListenerException httpEx)
                {
                    ServiceEventSource.Current.Message($"HttpListenerException: {httpEx.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    ServiceEventSource.Current.HttpListenerError($"Error in ProcessRequestsAsync: {ex.Message}");
                }
            }

            ServiceEventSource.Current.Message("ProcessRequestsAsync ended");
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                ServiceEventSource.Current.ServiceRequestStart(context.Request.Url.AbsolutePath);

                var request = context.Request;
                var response = context.Response;
                string responseText = "";

                switch (request.Url.AbsolutePath.ToLower())
                {
                    case "/health-dashboard":
                        responseText = await GenerateHealthDashboard();
                        response.ContentType = "text/html";
                        break;
                    case "/service-fabric-explorer":
                        responseText = GenerateServiceFabricExplorerRedirect();
                        response.ContentType = "text/html";
                        break;
                    case "/health":
                        responseText = GenerateHealthJson();
                        response.ContentType = "application/json";
                        break;
                    case "/export":
                        responseText = await GenerateExport(request.QueryString["format"], response);
                        break;
                    case "/test":
                        responseText = GenerateTestPage();
                        response.ContentType = "text/html";
                        break;
                    case "/":
                    default:
                        responseText = GenerateHomePage();
                        response.ContentType = "text/html";
                        break;
                }

                var buffer = Encoding.UTF8.GetBytes(responseText);
                response.ContentLength64 = buffer.Length;

                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();

                ServiceEventSource.Current.DashboardRequestHandled();
                ServiceEventSource.Current.Message($"Successfully handled request to {request.Url.AbsolutePath}");
            }
            catch (Exception ex)
            {
                ServiceEventSource.Current.ServiceRequestFailed(context.Request.Url.AbsolutePath, ex.Message);
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private async Task<string> GenerateHealthDashboard()
        {
            try
            {
                FabricClient fabricClient = null;
                try
                {
                    fabricClient = new FabricClient();
                }
                catch (Exception ex)
                {
                    ServiceEventSource.Current.FabricClientCreationFailed(ex.Message);
                }

                var dashboardService = new DashboardService(fabricClient, _serviceContext);
                return await dashboardService.GenerateDashboardHtml();
            }
            catch (Exception ex)
            {
                // Log the full exception server-side; the page itself stays generic.
                ServiceEventSource.Current.DashboardGenerationFailed(ex.ToString());
                return GenerateErrorPage("Health Dashboard", ex);
            }
        }

        private string GenerateServiceFabricExplorerRedirect()
        {
            var body = @"
    <div class='container'>
        <div class='page-head'><h1>Service Fabric Explorer</h1></div>
        <div class='card'>
            <p>Redirecting to Service Fabric Explorer in 3 seconds...</p>
            <p class='muted'>If it doesn't open automatically, use the link below.</p>
            <div class='nav-links'>
                <a class='btn' href='http://localhost:19080' target='_blank'>Open Service Fabric Explorer</a>
                <a class='btn' href='health-dashboard'>Back to dashboard</a>
            </div>
            <p class='muted' style='margin-top:14px'>Explorer URL: http://localhost:19080 (opens in a new tab).</p>
        </div>
    </div>";
            var head = @"<script>setTimeout(function(){ window.open('http://localhost:19080', '_blank'); }, 3000);</script>";
            return HealthUi.Layout("Service Fabric Explorer", body, head);
        }

        // Builds a downloadable cluster-health snapshot and sets the attachment headers. The body
        // is returned to HandleRequest, which writes it as UTF-8 (matching the other routes).
        private async Task<string> GenerateExport(string format, HttpListenerResponse response)
        {
            FabricClient fabricClient = null;
            try { fabricClient = new FabricClient(); }
            catch (Exception ex) { ServiceEventSource.Current.FabricClientCreationFailed(ex.Message); }

            var dashboardService = new DashboardService(fabricClient, _serviceContext);
            var export = await dashboardService.GenerateExportAsync(format);
            response.ContentType = export.ContentType;
            response.AddHeader("Content-Disposition", $"attachment; filename=\"{export.FileName}\"");
            return export.Body;
        }

        private static readonly JsonSerializerOptions HealthJsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private string GenerateHealthJson()
        {
            var payload = new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                serviceName = _serviceContext.ServiceName.ToString(),
                nodeName = _serviceContext.NodeContext.NodeName,
                instanceId = _serviceContext.InstanceId,
                applicationName = _serviceContext.CodePackageActivationContext.ApplicationName,
                applicationTypeName = _serviceContext.CodePackageActivationContext.ApplicationTypeName,
                version = _serviceContext.CodePackageActivationContext.CodePackageVersion,
            };
            return JsonSerializer.Serialize(payload, HealthJsonOptions);
        }

        private string GenerateTestPage()
        {
            var body = $@"
    <div class='container'>
        <div class='page-head'>
            <h1>Test Endpoint</h1>
            <div class='sub'>Service is running correctly.</div>
        </div>
        <div class='card'>
            <p class='muted'>Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
            <div class='info-grid'>
                <div class='info-item'><strong>Service</strong><br>{WebUtility.HtmlEncode(_serviceContext.ServiceName.ToString())}</div>
                <div class='info-item'><strong>Node</strong><br>{WebUtility.HtmlEncode(_serviceContext.NodeContext.NodeName)}</div>
                <div class='info-item'><strong>Instance ID</strong><br>{_serviceContext.InstanceId}</div>
                <div class='info-item'><strong>Application</strong><br>{WebUtility.HtmlEncode(_serviceContext.CodePackageActivationContext.ApplicationName)}</div>
            </div>
            <div class='nav-links'>
                <a class='btn' href='health-dashboard'>Health Dashboard</a>
                <a class='btn' href='health'>Health API</a>
                <a class='btn' href='.'>Home</a>
            </div>
        </div>
    </div>";
            return HealthUi.Layout("Test endpoint", body);
        }

        private string GenerateHomePage()
        {
            var body = $@"
    <div class='container'>
        <div class='page-head'>
            <h1>Service Fabric Health Dashboard</h1>
            <div class='sub'>Last updated {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
        </div>
        <div class='cards'>
            <div class='card'>
                <h3>Health Dashboard</h3>
                <p>Cluster health at a glance: applications, services, and nodes, plus system performance and hardware information.</p>
                <a class='btn' href='health-dashboard'>View health dashboard →</a>
            </div>
            <div class='card'>
                <h3>Health API</h3>
                <p>JSON endpoint for programmatic access to service health - useful for monitoring tools and automated systems.</p>
                <a class='btn' href='health'>View health API →</a>
            </div>
            <div class='card'>
                <h3>Test Endpoint</h3>
                <p>Verify service connectivity and view basic service information for troubleshooting and validation.</p>
                <a class='btn' href='test'>Run test →</a>
            </div>
        </div>
    </div>";
            return HealthUi.Layout("Home", body);
        }

        private string GenerateErrorPage(string pageName, Exception ex)
        {
            // Detail is logged via ServiceEventSource by the caller. The client only sees
            // a generic page — never the exception type, message, or stack trace.
            var body = $@"
    <div class='container'>
        <div class='page-head'><h1>{WebUtility.HtmlEncode(pageName)} unavailable</h1></div>
        <div class='banner err'>The dashboard could not render this page. The error has been logged for the operator.</div>
        <div class='card'>
            <p class='muted'>Time (UTC): {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</p>
            <div class='nav-links'>
                <a class='btn' href='health'>Health check</a>
                <a class='btn' href='test'>Test endpoint</a>
                <a class='btn' href='.'>Home</a>
            </div>
        </div>
    </div>";
            return HealthUi.Layout("Error", body);
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            ServiceEventSource.Current.Message("CloseAsync called");
            _cancellationTokenSource?.Cancel();
            _httpListener?.Stop();
            return Task.CompletedTask;
        }

        public void Abort()
        {
            ServiceEventSource.Current.Message("Abort called");
            _cancellationTokenSource?.Cancel();
            _httpListener?.Stop();
        }
    }
}