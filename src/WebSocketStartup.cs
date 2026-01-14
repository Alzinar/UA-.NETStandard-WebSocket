/* Copyright (c) 1996-2026 The OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation Corporate Members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

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
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
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
