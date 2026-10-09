namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Creates a new WebSocketTransportChannel with ITransportChannel interface.
    /// </summary>
    public class WebSocketTransportChannelFactory : ITransportChannelFactory
    {
        /// <summary>
        /// The protocol supported by the channel.
        /// </summary>
        public string UriScheme => Utils.UriSchemeOpcWss;

        /// <summary>
        /// The method creates a new instance of a WebSocket transport channel
        /// </summary>
        /// <returns>The transport channel</returns>
        public ITransportChannel Create(ITelemetryContext telemetry)
        {
            return new WebSocketTransportChannel(telemetry);
        }
    }
}
