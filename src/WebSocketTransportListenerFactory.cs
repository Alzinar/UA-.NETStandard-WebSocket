namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Creates a new <see cref="WebSocketTransportListener"/> with
    /// <see cref="ITransportListener"/> interface.
    /// </summary>
    public class WebSocketTransportListenerFactory : WebSocketServiceHost
    {
        /// <summary>
        /// The protocol supported by the listener.
        /// </summary>
        public override string UriScheme => Utils.UriSchemeOpcWss;

        /// <summary>
        /// The method creates a new instance of a <see cref="WebSocketTransportListener"/>.
        /// </summary>
        /// <returns>The transport listener.</returns>
        public override ITransportListener Create(ITelemetryContext telemetry)
        {
            return new WebSocketTransportListener(telemetry);
        }
    }
}
