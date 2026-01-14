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

using System;

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Handles async event callbacks from a WebSocket
    /// </summary>
    public class WebSocketMessageSocketAsyncEventArgs : IMessageSocketAsyncEventArgs
    {
        /// <summary>
        /// Create the event args for the async WebSocket message socket.
        /// </summary>
        public WebSocketMessageSocketAsyncEventArgs()
        {
            UserToken = this;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
        }

        /// <inheritdoc/>
        public object UserToken { get; set; }

        /// <inheritdoc/>
        public void SetBuffer(byte[] buffer, int offset, int count)
        {
            Buffer = buffer;
            Offset = offset;
            Count = count;
        }

        /// <inheritdoc/>
        public bool IsSocketError { get; set; }

        /// <inheritdoc/>
        public string SocketErrorString { get; set; }

        /// <inheritdoc/>
        public event EventHandler<IMessageSocketAsyncEventArgs> Completed;

        /// <inheritdoc/>
        public int BytesTransferred { get; set; }

        /// <inheritdoc/>
        public byte[] Buffer { get; set; }

        /// <inheritdoc/>
        public BufferCollection BufferList { get; set; }

        /// <summary>
        /// Offset in buffer.
        /// </summary>
        public int Offset { get; set; }

        /// <summary>
        /// Count of bytes.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Invoke completed event.
        /// </summary>
        internal void OnCompleted()
        {
            Completed?.Invoke(this, this);
        }
    }
}
