using System;
using System.Diagnostics.Tracing;
using System.Fabric;
using System.Threading.Tasks;

namespace TRPDashboard
{
    [EventSource(Name = "TRPDashboard-HealthMonitoring")]
    internal sealed class ServiceEventSource : EventSource
    {
        public static readonly ServiceEventSource Current = new ServiceEventSource();

        static ServiceEventSource()
        {
            // A workaround for the problem where ETW activities do not get tracked until Tasks infrastructure is initialized.
            // This problem will be fixed in .NET Framework 4.6.2.
            Task.Run(() => { });
        }

        // Instance constructor is private to enforce singleton semantics
        private ServiceEventSource() : base() { }

        #region Keywords
        // Event keywords can be used to categorize events. 
        // Each keyword is a bit flag. A single event can be associated with multiple keywords (via EventAttribute.Keywords property).
        // Keywords must be defined as a public class named 'Keywords' inside EventSource that uses them.
        public static class Keywords
        {
            public const EventKeywords Requests = (EventKeywords)0x1L;
            public const EventKeywords ServiceInitialization = (EventKeywords)0x2L;
            public const EventKeywords Errors = (EventKeywords)0x4L;
            public const EventKeywords Deployment = (EventKeywords)0x8L;
        }
        #endregion

        #region Events
        // Define an instance method for each event you want to record and apply an [Event] attribute to it.
        // The method name is the name of the event.
        // Pass any parameters you want to record with the event (only primitive integer types, DateTime, Guid & string are allowed).
        // Each event method implementation should check whether the event source is enabled, and if so, call WriteEvent() method to raise the event.
        // The number and types of arguments passed to every WriteEvent() method must exactly match the number and types of arguments passed to the corresponding event method.

        [NonEvent]
        public void Message(string message, params object[] args)
        {
            if (this.IsEnabled())
            {
                string finalMessage = string.Format(message, args);
                Message(finalMessage);
            }
        }

        [Event(1, Level = EventLevel.Informational, Message = "{0}")]
        public void Message(string message)
        {
            if (this.IsEnabled())
            {
                WriteEvent(1, message);
            }
        }

        [NonEvent]
        public void ServiceMessage(ServiceContext serviceContext, string message, params object[] args)
        {
            if (this.IsEnabled())
            {
                string finalMessage = string.Format(message, args);
                ServiceMessage(
                    serviceContext.ServiceName.ToString(),
                    serviceContext.ServiceTypeName,
                    GetReplicaOrInstanceId(serviceContext),
                    serviceContext.PartitionId,
                    serviceContext.CodePackageActivationContext.ApplicationName,
                    serviceContext.CodePackageActivationContext.ApplicationTypeName,
                    serviceContext.NodeContext.NodeName,
                    finalMessage);
            }
        }

        // For very high-frequency events it might be advantageous to raise events using WriteEventCore API.
        // This results in more efficient parameter handling, but requires explicit allocation of EventData structure and unsafe code.
        // To enable this code path, define UNSAFE conditional compilation symbol and turn on unsafe code support in project properties.
        [Event(2, Level = EventLevel.Informational, Message = "{7}")]
        private
#if UNSAFE
        unsafe
#endif
        void ServiceMessage(
            string serviceName,
            string serviceTypeName,
            long replicaOrInstanceId,
            Guid partitionId,
            string applicationName,
            string applicationTypeName,
            string nodeName,
            string message)
        {
#if !UNSAFE
            WriteEvent(2, serviceName, serviceTypeName, replicaOrInstanceId, partitionId, applicationName, applicationTypeName, nodeName, message);
#else
            const int numArgs = 8;
            fixed (char* pServiceName = serviceName, pServiceTypeName = serviceTypeName, pApplicationName = applicationName, pApplicationTypeName = applicationTypeName, pNodeName = nodeName, pMessage = message)
            {
                EventData* eventData = stackalloc EventData[numArgs];
                eventData[0] = new EventData { DataPointer = (IntPtr) pServiceName, Size = SizeInBytes(serviceName) };
                eventData[1] = new EventData { DataPointer = (IntPtr) pServiceTypeName, Size = SizeInBytes(serviceTypeName) };
                eventData[2] = new EventData { DataPointer = (IntPtr) (&replicaOrInstanceId), Size = sizeof(long) };
                eventData[3] = new EventData { DataPointer = (IntPtr) (&partitionId), Size = sizeof(Guid) };
                eventData[4] = new EventData { DataPointer = (IntPtr) pApplicationName, Size = SizeInBytes(applicationName) };
                eventData[5] = new EventData { DataPointer = (IntPtr) pApplicationTypeName, Size = SizeInBytes(applicationTypeName) };
                eventData[6] = new EventData { DataPointer = (IntPtr) pNodeName, Size = SizeInBytes(nodeName) };
                eventData[7] = new EventData { DataPointer = (IntPtr) pMessage, Size = SizeInBytes(message) };

                WriteEventCore(2, numArgs, eventData);
            }
#endif
        }

        private static long GetReplicaOrInstanceId(ServiceContext serviceContext)
        {
            StatelessServiceContext stateless = serviceContext as StatelessServiceContext;
            if (stateless != null)
            {
                return stateless.InstanceId;
            }

            StatefulServiceContext stateful = serviceContext as StatefulServiceContext;
            if (stateful != null)
            {
                return stateful.ReplicaId;
            }

            throw new NotSupportedException("Context type not supported.");
        }

#if UNSAFE
        private static int SizeInBytes(string s)
        {
            if (s == null)
            {
                return 0;
            }
            else
            {
                return (s.Length + 1) * sizeof(char);
            }
        }
#endif

        [Event(3, Level = EventLevel.Informational, Message = "Service host process {0} registered service type {1}", Keywords = Keywords.ServiceInitialization)]
        public void ServiceTypeRegistered(int hostProcessId, string serviceType)
        {
            WriteEvent(3, hostProcessId, serviceType);
        }

        [Event(4, Level = EventLevel.Error, Message = "Service host initialization failed", Keywords = Keywords.ServiceInitialization)]
        public void ServiceHostInitializationFailed(string exception)
        {
            WriteEvent(4, exception);
        }

        [Event(5, Level = EventLevel.Informational, Message = "Service request start", Keywords = Keywords.Requests)]
        public void ServiceRequestStart(string requestTypeName)
        {
            WriteEvent(5, requestTypeName);
        }

        [Event(6, Level = EventLevel.Informational, Message = "Service request stop", Keywords = Keywords.Requests)]
        public void ServiceRequestStop(string requestTypeName, string exception = "")
        {
            WriteEvent(6, requestTypeName, exception);
        }

        [Event(7, Level = EventLevel.Informational, Message = "Service request failed", Keywords = Keywords.Requests)]
        public void ServiceRequestFailed(string requestTypeName, string exception)
        {
            WriteEvent(7, requestTypeName, exception);
        }

        // Additional events for better debugging
        [Event(8, Level = EventLevel.Error, Message = "HTTP Listener error: {0}", Keywords = Keywords.Errors)]
        public void HttpListenerError(string error)
        {
            WriteEvent(8, error);
        }

        [Event(9, Level = EventLevel.Informational, Message = "HTTP Listener started on: {0}", Keywords = Keywords.ServiceInitialization)]
        public void HttpListenerStarted(string address)
        {
            WriteEvent(9, address);
        }

        [Event(10, Level = EventLevel.Warning, Message = "FabricClient creation failed: {0}", Keywords = Keywords.Errors)]
        public void FabricClientCreationFailed(string error)
        {
            WriteEvent(10, error);
        }

        [Event(11, Level = EventLevel.Informational, Message = "Dashboard request handled successfully", Keywords = Keywords.Requests)]
        public void DashboardRequestHandled()
        {
            WriteEvent(11);
        }

        [Event(12, Level = EventLevel.Error, Message = "Dashboard generation failed: {0}", Keywords = Keywords.Errors)]
        public void DashboardGenerationFailed(string error)
        {
            WriteEvent(12, error);
        }

        [Event(13, Level = EventLevel.Informational, Message = "Service deployment started: {0}", Keywords = Keywords.Deployment)]
        public void ServiceDeploymentStarted(string version)
        {
            WriteEvent(13, version);
        }

        [Event(14, Level = EventLevel.Informational, Message = "Service deployment completed: {0}", Keywords = Keywords.Deployment)]
        public void ServiceDeploymentCompleted(string version)
        {
            WriteEvent(14, version);
        }

        [Event(15, Level = EventLevel.Error, Message = "Service deployment failed: {0}", Keywords = Keywords.Deployment)]
        public void ServiceDeploymentFailed(string error)
        {
            WriteEvent(15, error);
        }

        #endregion
    }
}