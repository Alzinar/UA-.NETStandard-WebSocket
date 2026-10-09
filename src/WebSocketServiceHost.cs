using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Opc.Ua.Security.Certificates;

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Creates a new <see cref="WebSocketTransportListener"/> with
    /// <see cref="ITransportListener"/> interface.
    /// </summary>
    public abstract class WebSocketServiceHost : ITransportListenerFactory
    {
        /// <summary>
        /// The protocol supported by the listener.
        /// </summary>
        public abstract string UriScheme { get; }

        /// <summary>
        /// The method creates a new instance of a <see cref="WebSocketTransportListener"/>.
        /// </summary>
        /// <returns>The transport listener.</returns>
        public abstract ITransportListener Create(ITelemetryContext telemetry);

        /// <inheritdoc/>
        /// <summary>
        /// Create a new service host for UA WebSocket.
        /// </summary>
        public List<EndpointDescription> CreateServiceHost(
            ServerBase serverBase,
            IDictionary<string, ServiceHost> hosts,
            ApplicationConfiguration configuration,
            IList<string> baseAddresses,
            ApplicationDescription serverDescription,
            List<ServerSecurityPolicy> securityPolicies,
            CertificateTypesProvider instanceCertificateTypesProvider)
        {
            // generate a unique host name.
            string hostName = "/WebSocket";

            if (hosts.ContainsKey(hostName))
            {
                hostName += Utils.Format("/{0}", hosts.Count);
            }

            // build list of uris.
            var uris = new List<Uri>();
            var endpoints = new EndpointDescriptionCollection();

            // create the endpoint configuration to use.
            var endpointConfiguration = EndpointConfiguration.Create(configuration);
            string computerName = Utils.GetHostName();

            // create intermediate logger for just this call.
            ILogger logger = serverBase.MessageContext.Telemetry.CreateLogger<WebSocketServiceHost>();

            for (int ii = 0; ii < baseAddresses.Count; ii++)
            {
                if (!baseAddresses[ii].StartsWith(Utils.UriSchemeOpcWss, StringComparison.Ordinal))
                {
                    continue;
                }

                var uri = new UriBuilder(baseAddresses[ii]);

                if (uri.Path[^1] != '/')
                {
                    uri.Path += "/";
                }

                if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    uri.Host = computerName;
                }

                ITransportListener listener = Create(serverBase.MessageContext.Telemetry);
                if (listener != null)
                {

                    var listenerEndpoints = new EndpointDescriptionCollection();
                    uris.Add(uri.Uri);

                    foreach (ServerSecurityPolicy policy in securityPolicies)
                    {
                        // create the endpoint description.
                        var description = new EndpointDescription
                        {
                            EndpointUrl = uri.ToString(),
                            Server = serverDescription,
                            TransportProfileUri = Profiles.UaWssTransport,
                            SecurityMode = policy.SecurityMode,
                            SecurityPolicyUri = policy.SecurityPolicyUri,
                            SecurityLevel = ServerSecurityPolicy.CalculateSecurityLevel(
                                policy.SecurityMode,
                                policy.SecurityPolicyUri,
                                logger)
                        };
                        description.UserIdentityTokens = serverBase.GetUserTokenPolicies(
                            configuration,
                            description);

                        ServerBase.SetServerCertificateInEndpointDescription(
                            description,
                            instanceCertificateTypesProvider);

                        listenerEndpoints.Add(description);
                    }

                    serverBase.CreateServiceHostEndpoint(
                        uri.Uri,
                        listenerEndpoints,
                        endpointConfiguration,
                        listener,
                        configuration.CertificateValidator.GetChannelValidator());

                    endpoints.AddRange(listenerEndpoints);
                }
                else
                {
                    logger.LogError("Failed to create endpoint {Uri} because the transport profile is unsupported.", uri);
                }
            }

            return endpoints;
        }
    }
}
