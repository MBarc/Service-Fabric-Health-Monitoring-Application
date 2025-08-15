using System;
using System.Collections.Generic;
using System.Fabric;
using System.Net;
using System.Text;
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

        public Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                ServiceEventSource.Current.Message("=== SimpleHttpCommunicationListener starting ===");

                // Use a fixed port for testing
                int port = 8081;

                // Try to get port from endpoint, fallback to 8081
                try
                {
                    var endpoint = _serviceContext.CodePackageActivationContext.GetEndpoint("ServiceEndpoint");
                    port = endpoint.Port;
                    ServiceEventSource.Current.Message($"Got port from endpoint: {port}");
                }
                catch (Exception ex)
                {
                    ServiceEventSource.Current.Message($"Could not get endpoint port, using default 8081: {ex.Message}");
                    port = 8081;
                }

                var listeningAddress = $"http://+:{port}/";
                _publishAddress = $"http://localhost:{port}/";

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
                ServiceEventSource.Current.DashboardGenerationFailed(ex.Message);
                return GenerateErrorPage("Health Dashboard", ex);
            }
        }

        private string GenerateServiceFabricExplorerRedirect()
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <title>Service Fabric Explorer - Redirect</title>
    <style>
        body {{ font-family: 'Segoe UI', sans-serif; margin: 40px; background: #f8f9fa; }}
        .container {{ max-width: 600px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        h1 {{ color: #003f7f; }}
        .redirect-info {{ background: #e8f4f8; padding: 20px; border-radius: 6px; margin: 20px 0; }}
        .link {{ display: inline-block; padding: 10px 20px; background: #003f7f; color: white; text-decoration: none; border-radius: 4px; margin: 10px 5px; }}
        .link:hover {{ background: #0066cc; }}
    </style>
    <script>
        // Auto-redirect after 3 seconds
        setTimeout(function() {{
            window.open('http://localhost:19080', '_blank');
        }}, 3000);
    </script>
</head>
<body>
    <div class='container'>
        <h1>Service Fabric Explorer</h1>
        <div class='redirect-info'>
            <p><strong>Redirecting to Service Fabric Explorer...</strong></p>
            <p>You will be redirected to the Service Fabric Explorer in 3 seconds.</p>
            <p>If the redirect doesn't work, click the link below:</p>
        </div>
        <a href='http://localhost:19080' target='_blank' class='link'>Open Service Fabric Explorer</a>
        <a href='/health-dashboard' class='link'>Back to Dashboard</a>
        
        <div style='margin-top: 30px; color: #6c757d; font-size: 0.9em;'>
            <p><strong>Service Fabric Explorer URL:</strong> http://localhost:19080</p>
            <p>This will open in a new tab/window for easy navigation between the dashboard and explorer.</p>
        </div>
    </div>
</body>
</html>";
        }

        private string GenerateHealthJson()
        {
            return $@"{{
    ""status"": ""healthy"",
    ""timestamp"": ""{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}"",
    ""serviceName"": ""{_serviceContext.ServiceName}"",
    ""nodeName"": ""{_serviceContext.NodeContext.NodeName}"",
    ""instanceId"": ""{_serviceContext.InstanceId}"",
    ""applicationName"": ""{_serviceContext.CodePackageActivationContext.ApplicationName}"",
    ""applicationTypeName"": ""{_serviceContext.CodePackageActivationContext.ApplicationTypeName}"",
    ""version"": ""1.0.11""
}}";
        }

        private string GenerateTestPage()
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <title>TRP Dashboard - Test Endpoint</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; background: #f5f5f5; }}
        .container {{ max-width: 800px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        h1 {{ color: #003087; }}
        .info-grid {{ display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin: 20px 0; }}
        .info-item {{ padding: 10px; background: #f8f9fa; border-left: 4px solid #0066CC; }}
        .nav-links {{ margin-top: 30px; }}
        .nav-links a {{ display: inline-block; margin-right: 20px; padding: 10px 20px; background: #003087; color: white; text-decoration: none; border-radius: 4px; }}
        .nav-links a:hover {{ background: #0066CC; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>TRP Dashboard - Test Endpoint</h1>
        <p><strong>Status:</strong> Service is running correctly!</p>
        <p><strong>Time:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
        
        <div class='info-grid'>
            <div class='info-item'>
                <strong>Service Name:</strong><br>{_serviceContext.ServiceName}
            </div>
            <div class='info-item'>
                <strong>Node Name:</strong><br>{_serviceContext.NodeContext.NodeName}
            </div>
            <div class='info-item'>
                <strong>Instance ID:</strong><br>{_serviceContext.InstanceId}
            </div>
            <div class='info-item'>
                <strong>Application:</strong><br>{_serviceContext.CodePackageActivationContext.ApplicationName}
            </div>
        </div>
        
        <div class='nav-links'>
            <a href='/health-dashboard'>Health Dashboard</a>
            <a href='/health'>Health API</a>
            <a href='/'>Home</a>
        </div>
    </div>
</body>
</html>";
        }

        private string GenerateHomePage()
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <title>TRP Dashboard - Home</title>
    <style>
        body {{ font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 0; background: linear-gradient(135deg, #0066CC 0%, #003087 100%); min-height: 100vh; }}
        .container {{ max-width: 1000px; margin: 0 auto; padding: 40px 20px; }}
        .header {{ background: white; padding: 40px; border-radius: 12px; box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1); margin-bottom: 30px; text-align: center; border-left: 6px solid #003087; }}
        .header h1 {{ color: #003087; font-size: 2.5em; margin-bottom: 10px; font-weight: 700; }}
        .header h2 {{ color: #001F5C; font-size: 1.5em; margin-bottom: 20px; font-weight: 400; }}
        .cards {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 20px; }}
        .card {{ background: white; padding: 30px; border-radius: 12px; box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1); border-left: 6px solid #0066CC; }}
        .card h3 {{ color: #003087; margin-bottom: 15px; }}
        .card p {{ color: #6B7280; line-height: 1.6; }}
        .btn {{ display: inline-block; padding: 12px 24px; background: #003087; color: white; text-decoration: none; border-radius: 6px; margin-top: 15px; font-weight: 600; }}
        .btn:hover {{ background: #0066CC; }}
        .status {{ background: #10B981; color: white; padding: 4px 12px; border-radius: 20px; font-size: 0.9em; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>Internal Department Name</h1>
            <h2>TRP Documentation Dashboard</h2>
            <span class='status'>Service Running</span>
            <p style='margin-top: 15px; color: #6B7280;'>Last Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
        </div>
        
        <div class='cards'>
            <div class='card'>
                <h3>Health Dashboard</h3>
                <p>Comprehensive view of Service Fabric cluster health, including applications, services, and nodes. Monitor system performance and hardware information.</p>
                <a href='/health-dashboard' class='btn'>View Health Dashboard</a>
            </div>
            
            <div class='card'>
                <h3>Health API</h3>
                <p>JSON endpoint for programmatic access to service health information. Useful for monitoring tools and automated systems.</p>
                <a href='/health' class='btn'>View Health API</a>
            </div>
            
            <div class='card'>
                <h3>Test Endpoint</h3>
                <p>Verify service connectivity and view basic service information. Useful for troubleshooting and service validation.</p>
                <a href='/test' class='btn'>Run Test</a>
            </div>
        </div>
    </div>
</body>
</html>";
        }

        private string GenerateErrorPage(string pageName, Exception ex)
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <title>TRP Dashboard - Error</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; background: #f8f9fa; }}
        .container {{ max-width: 800px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        .error {{ background-color: #f8d7da; border: 1px solid #f5c6cb; padding: 20px; border-radius: 5px; margin: 20px 0; }}
        pre {{ background-color: #f8f9fa; padding: 15px; border-radius: 5px; overflow-x: auto; font-size: 0.9em; }}
        h1 {{ color: #721c24; }}
        .nav-links a {{ display: inline-block; margin-right: 15px; padding: 8px 16px; background: #007bff; color: white; text-decoration: none; border-radius: 4px; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>TRP Dashboard - {pageName} Error</h1>
        <div class='error'>
            <h2>Error: {ex.GetType().Name}</h2>
            <p><strong>Message:</strong> {ex.Message}</p>
        </div>
        <h3>Stack Trace:</h3>
        <pre>{ex.StackTrace}</pre>
        <p><strong>Time:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
        <div class='nav-links'>
            <a href='/health'>Health Check</a>
            <a href='/test'>Test Endpoint</a>
            <a href='/'>Home</a>
        </div>
    </div>
</body>
</html>";
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