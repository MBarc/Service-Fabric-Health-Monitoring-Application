using Microsoft.ServiceFabric.Services.Runtime;
using System;
using System.Diagnostics;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace TRPDashboard
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static void Main()
        {
            try
            {
                // Enhanced logging
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] TRPDashboard starting...");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TRPDashboard starting...");

                // Log environment info
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Working Directory: {Directory.GetCurrentDirectory()}");
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Machine Name: {Environment.MachineName}");
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Process ID: {Environment.ProcessId}");

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Registering service...");

                ServiceRuntime.RegisterServiceAsync("TRPDashboardType",
                    context =>
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service factory called for context: {context.ServiceName}");
                        return new TRPDashboard(context);
                    }).GetAwaiter().GetResult();

                ServiceEventSource.Current.ServiceTypeRegistered(Process.GetCurrentProcess().Id, typeof(TRPDashboard).Name);

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] TRPDashboard service registered successfully");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TRPDashboard service registered successfully");

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entering infinite sleep to keep process alive");

                // THIS is what keeps the service running
                Thread.Sleep(Timeout.Infinite);
            }
            catch (FabricException ex) when (ex.ErrorCode == FabricErrorCode.ServiceTypeAlreadyRegistered)
            {
                // This is the restart loop - log it and exit gracefully
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service type already registered - this is a restart. Process ID: {Environment.ProcessId}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service type already registered - this is a restart. Process ID: {Environment.ProcessId}");

                // Instead of exiting immediately, wait a bit to allow proper cleanup
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for cleanup and then continuing...");
                Thread.Sleep(2000); // Wait 2 seconds

                // Now continue normally instead of exiting
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entering infinite sleep (restart scenario)");
                Thread.Sleep(Timeout.Infinite);
            }
            catch (FabricException ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] FabricException: {ex.ErrorCode} - {ex.Message}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FabricException: {ex.ErrorCode} - {ex.Message}");
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stack trace: {ex.StackTrace}");
                ServiceEventSource.Current.ServiceHostInitializationFailed(ex.ToString());
                throw;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Program.Main ERROR: {e.GetType().Name} - {e.Message}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Program.Main ERROR: {e.GetType().Name} - {e.Message}");
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stack trace: {e.StackTrace}");

                if (e.InnerException != null)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Inner exception: {e.InnerException.Message}");
                }

                ServiceEventSource.Current.ServiceHostInitializationFailed(e.ToString());
                throw;
            }
        }
    }
}