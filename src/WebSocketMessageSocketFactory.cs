namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Creates a new WebSocketMessageSocket with IMessageSocket interface.
    /// </summary>
    public class WebSocketMessageSocketFactory : IMessageSocketFactory
    {
        /// <summary>
        /// Create a socket factory
        /// </summary>
        /// <param name="telemetry">The telemetry context to use to create observability instruments</param>
        public WebSocketMessageSocketFactory(ITelemetryContext telemetry)
        {
            m_telemetry = telemetry;
        }

        /// <summary>
        /// The method creates a new instance of a WebSocket message socket
        /// </summary>
        /// <returns>the message socket</returns>
        public IMessageSocket Create(
            IMessageSink sink,
            BufferManager bufferManager,
            int receiveBufferSize)
        {
            return new WebSocketMessageSocket(
                sink,
                bufferManager,
                receiveBufferSize,
                m_telemetry)
            {
                CertificateValidator = CertificateValidator
            };
        }

        /// <summary>
        /// Validates the server's TLS certificate for sockets created by this factory.
        /// </summary>
        public ICertificateValidator CertificateValidator { get; set; }

        /// <summary>
        /// Gets the implementation description.
        /// </summary>
        /// <value>The implementation string.</value>
        public string Implementation => "UA-WSS";

        private readonly ITelemetryContext m_telemetry;
    }
}
