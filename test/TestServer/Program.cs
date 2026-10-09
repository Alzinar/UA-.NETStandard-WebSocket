using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Bindings;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;
using Opc.Ua.Server;

namespace TestServer
{
    internal static class Program
    {
        private const int Port = Utils.UaWebSocketsDefaultPort;
        private static readonly string EndpointUrl = $"opc.wss://{Utils.GetHostName()}:{Port}/UaWebSocketTest";

        private static async Task<int> Main()
        {
            ITelemetryContext telemetry = DefaultTelemetry.Create(builder => builder.AddConsole());

            var configuration = new ApplicationConfiguration(telemetry)
            {
                ApplicationName = "UaWebSocket Test Server",
                ApplicationUri = "urn:localhost:UaWebSocketTestServer",
                ProductUri = "https://github.com/Alzinar/UA-.NETStandard-WebSocket",
                ApplicationType = ApplicationType.Server,
                TransportQuotas = new TransportQuotas(),
                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { EndpointUrl },
                    SecurityPolicies =
                    {
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None
                        },
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.SignAndEncrypt,
                            SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                        }
                    },
                    UserTokenPolicies =
                    {
                        new UserTokenPolicy { TokenType = UserTokenType.Anonymous, PolicyId = "Anonymous" }
                    },
                    // no local discovery server is running for this test
                    MaxRegistrationInterval = 0
                }
            };

            configuration.SecurityConfiguration.ApplicationCertificates.Add(
                new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = "CN=UaWebSocket Test Server, O=UaWebSocket"
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
            // self-signed certs are expected for this local test
            configuration.SecurityConfiguration.AutoAcceptUntrustedCertificates = true;

            var application = new ApplicationInstance(telemetry)
            {
                ApplicationName = configuration.ApplicationName,
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = configuration
            };

            await application.CheckApplicationInstanceCertificatesAsync(silent: true).ConfigureAwait(false);

            // the WebSocket transport must be registered before the server starts
            TransportBindings.Listeners.SetBinding(new WebSocketTransportListenerFactory());

            var server = new StandardServer();
            server.AddNodeManager(new TestNodeManagerFactory());

            await application.StartAsync(server).ConfigureAwait(false);

            Console.WriteLine($"Server started. Listening on {EndpointUrl}");
            Console.WriteLine("Press Ctrl+C to exit.");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // triggered by Ctrl+C
            }

            await application.StopAsync().ConfigureAwait(false);
            return 0;
        }
    }
}
