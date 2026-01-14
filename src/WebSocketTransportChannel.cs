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

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Creates a transport channel with WebSocket transport, UA-SC security and UA Binary encoding
    /// </summary>
    public class WebSocketTransportChannel : UaSCUaBinaryTransportChannel
    {
        /// <summary>
        /// Create a WebSocket transport channel.
        /// </summary>
        public WebSocketTransportChannel(ITelemetryContext telemetry)
            : base(new WebSocketMessageSocketFactory(telemetry), telemetry)
        {
        }
    }
}
