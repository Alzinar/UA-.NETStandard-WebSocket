using System;
using System.Threading;
using System.Threading.Tasks;

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Creates a transport channel with WebSocket transport, UA-SC security and UA Binary encoding
    /// </summary>
    public class WebSocketTransportChannel : UaSCUaBinaryTransportChannel, ITransportChannel, ISecureChannel
    {
        /// <summary>
        /// Create a WebSocket transport channel.
        /// </summary>
        public WebSocketTransportChannel(ITelemetryContext telemetry)
            : this(new WebSocketMessageSocketFactory(telemetry), telemetry)
        {
        }

        private WebSocketTransportChannel(
            WebSocketMessageSocketFactory factory,
            ITelemetryContext telemetry)
            : base(factory, telemetry)
        {
            m_factory = factory;
        }

        // The base OpenAsync is not virtual; re-implementing ITransportChannel makes calls
        // through the interface (as the SDK client does) reach these methods.
        /// <inheritdoc/>
        public new ValueTask OpenAsync(Uri url, TransportChannelSettings settings, CancellationToken ct = default)
        {
            m_factory.CertificateValidator = settings?.CertificateValidator;
            return base.OpenAsync(url, settings, ct);
        }

        /// <inheritdoc/>
        public new ValueTask OpenAsync(
            ITransportWaitingConnection connection,
            TransportChannelSettings settings,
            CancellationToken ct = default)
        {
            m_factory.CertificateValidator = settings?.CertificateValidator;
            return base.OpenAsync(connection, settings, ct);
        }

        private readonly WebSocketMessageSocketFactory m_factory;
    }
}
