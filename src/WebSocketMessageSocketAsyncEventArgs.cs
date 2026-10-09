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
