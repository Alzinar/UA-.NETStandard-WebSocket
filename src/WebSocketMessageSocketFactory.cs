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
                m_telemetry);
        }

        /// <summary>
        /// Gets the implementation description.
        /// </summary>
        /// <value>The implementation string.</value>
        public string Implementation => "UA-WSS";

        private readonly ITelemetryContext m_telemetry;
    }
}
