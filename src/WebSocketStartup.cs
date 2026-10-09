using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Implements the Kestrel startup of the WebSocket listener.
    /// </summary>
    public class WebSocketStartup
    {
        /// <summary>
        /// Get the WebSocket listener.
        /// </summary>
        public static WebSocketTransportListener Listener { get; set; }

        /// <summary>
        /// Configure the request pipeline for the listener.
        /// </summary>
        /// <param name="appBuilder">The application builder.</param>
        public void Configure(IApplicationBuilder appBuilder)
        {
#if NET9_0_OR_GREATER
            // Enable WebSocket support
            appBuilder.UseWebSockets();
#endif
            
            appBuilder.Run(async context =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    // Accept WebSocket with OPC UA binary encoding subprotocol
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync("opcua+uacp").ConfigureAwait(false);
                    await Listener.HandleWebSocketConnectionAsync(context, webSocket).ConfigureAwait(false);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            });
        }
    }
}
