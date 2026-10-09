using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Bindings;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

namespace TestClient
{
    internal static class Program
    {
        private static readonly string EndpointUrl =
            $"opc.wss://{Utils.GetHostName()}:4843/UaWebSocketTest";
        private const string TestNamespaceUri = "http://test.local/UaWebSocket";
        private const int ExpectedValue = 42;

        private static async Task<int> Main()
        {
            ITelemetryContext telemetry = DefaultTelemetry.Create(builder => builder.AddConsole());

            var configuration = new ApplicationConfiguration(telemetry)
            {
                ApplicationName = "UaWebSocket Test Client",
                ApplicationUri = "urn:localhost:UaWebSocketTestClient",
                ProductUri = "https://github.com/Alzinar/UA-.NETStandard-WebSocket",
                ApplicationType = ApplicationType.Client,
                TransportQuotas = new TransportQuotas(),
                ClientConfiguration = new ClientConfiguration()
            };

            configuration.SecurityConfiguration.ApplicationCertificates.Add(
                new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = "CN=UaWebSocket Test Client, O=UaWebSocket"
                });
            configuration.SecurityConfiguration.TrustedPeerCertificates.StoreType = CertificateStoreType.Directory;
            configuration.SecurityConfiguration.TrustedPeerCertificates.StorePath = "pki/trusted";
            configuration.SecurityConfiguration.TrustedIssuerCertificates.StoreType = CertificateStoreType.Directory;
            configuration.SecurityConfiguration.TrustedIssuerCertificates.StorePath = "pki/issuer";
            configuration.SecurityConfiguration.RejectedCertificateStore = new CertificateStoreIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = "pki/rejected"
            };
            // the server uses a self-signed cert for this local test
            configuration.SecurityConfiguration.AutoAcceptUntrustedCertificates = true;
            configuration.CertificateValidator.CertificateValidation += (_, e) =>
            {
                if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
                {
                    e.Accept = true;
                }
            };

            var application = new ApplicationInstance(telemetry)
            {
                ApplicationName = configuration.ApplicationName,
                ApplicationType = ApplicationType.Client,
                ApplicationConfiguration = configuration
            };

            await application.CheckApplicationInstanceCertificatesAsync(silent: true).ConfigureAwait(false);

            // the WebSocket transport must be registered before connecting
            ((ITransportBindings<ITransportChannelFactory>)TransportBindings.Channels).SetBinding(
                new WebSocketTransportChannelFactory());

            EndpointDescription selectedEndpoint = await SelectSecureEndpointAsync(
                configuration,
                telemetry).ConfigureAwait(false);

            if (selectedEndpoint == null)
            {
                Console.WriteLine("FAIL: could not discover a Sign&Encrypt endpoint at " + EndpointUrl);
                return 1;
            }

            var endpoint = new ConfiguredEndpoint(
                null,
                selectedEndpoint,
                EndpointConfiguration.Create(configuration));

            var sessionFactory = new DefaultSessionFactory(telemetry);
            ISession session = await sessionFactory.CreateAsync(
                configuration,
                endpoint,
                updateBeforeConnect: false,
                sessionName: "TestClient",
                sessionTimeout: 60000,
                identity: new UserIdentity(),
                preferredLocales: null).ConfigureAwait(false);

            try
            {
                int namespaceIndex = session.NamespaceUris.GetIndex(TestNamespaceUri);
                if (namespaceIndex < 0)
                {
                    Console.WriteLine("FAIL: server does not expose namespace " + TestNamespaceUri);
                    return 1;
                }

                var nodeId = new NodeId("Int32Value", (ushort)namespaceIndex);

                int firstValue = await ReadInt32Async(session, nodeId).ConfigureAwait(false);
                Console.WriteLine($"Read (before write): {firstValue}");

                await WriteInt32Async(session, nodeId, ExpectedValue).ConfigureAwait(false);
                Console.WriteLine($"Wrote: {ExpectedValue}");

                int secondValue = await ReadInt32Async(session, nodeId).ConfigureAwait(false);
                Console.WriteLine($"Read (after write): {secondValue}");

                if (secondValue == ExpectedValue)
                {
                    Console.WriteLine("PASS");
                    return 0;
                }

                Console.WriteLine($"FAIL: expected {ExpectedValue} but read {secondValue}");
                return 1;
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
                session.Dispose();
            }
        }

        private static async Task<EndpointDescription> SelectSecureEndpointAsync(
            ApplicationConfiguration configuration,
            ITelemetryContext telemetry)
        {
            using DiscoveryClient discoveryClient = await DiscoveryClient.CreateAsync(
                configuration,
                new Uri(EndpointUrl),
                DiagnosticsMasks.None,
                CancellationToken.None).ConfigureAwait(false);

            EndpointDescriptionCollection endpoints = await discoveryClient
                .GetEndpointsAsync(null)
                .ConfigureAwait(false);

            foreach (EndpointDescription endpoint in endpoints)
            {
                if (endpoint.SecurityMode == MessageSecurityMode.SignAndEncrypt &&
                    endpoint.SecurityPolicyUri == SecurityPolicies.Basic256Sha256)
                {
                    return endpoint;
                }
            }

            return null;
        }

        private static async Task<int> ReadInt32Async(ISession session, NodeId nodeId)
        {
            var nodesToRead = new ReadValueIdCollection
            {
                new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value }
            };

            ReadResponse response = await session
                .ReadAsync(null, 0, TimestampsToReturn.Neither, nodesToRead, CancellationToken.None)
                .ConfigureAwait(false);

            DataValue result = response.Results[0];
            if (StatusCode.IsBad(result.StatusCode))
            {
                throw new ServiceResultException(result.StatusCode, $"Failed to read {nodeId}.");
            }

            return (int)result.Value;
        }

        private static async Task WriteInt32Async(ISession session, NodeId nodeId, int value)
        {
            var nodesToWrite = new WriteValueCollection
            {
                new WriteValue
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                }
            };

            WriteResponse response = await session
                .WriteAsync(null, nodesToWrite, CancellationToken.None)
                .ConfigureAwait(false);

            StatusCode result = response.Results[0];
            if (StatusCode.IsBad(result))
            {
                throw new ServiceResultException(result, $"Failed to write {nodeId}.");
            }
        }
    }
}
