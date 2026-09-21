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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Opc.Ua.Bindings
{

    /// <summary>
    /// Handles reading and writing of message chunks over a WebSocket.
    /// </summary>
    public class WebSocketMessageSocket : IMessageSocket
    {
        /// <summary>
        /// Creates an unconnected socket.
        /// </summary>
        public WebSocketMessageSocket(
            IMessageSink sink,
            BufferManager bufferManager,
            int receiveBufferSize,
            ITelemetryContext telemetry)
        {
            m_logger = telemetry.CreateLogger<WebSocketMessageSocket>();
            m_sink = sink;
            m_bufferManager = bufferManager;
            m_receiveBufferSize = receiveBufferSize;
        }
        /// <summary>
        /// Creates an unconnected socket.
        /// </summary>
        public WebSocketMessageSocket(
            WebSocket socket,
            IMessageSink sink,
            BufferManager bufferManager,
            int receiveBufferSize,
            ITelemetryContext telemetry)
        {
            m_webSocket = socket;
            m_logger = telemetry.CreateLogger<WebSocketMessageSocket>();
            m_sink = sink;
            m_bufferManager = bufferManager;
            m_receiveBufferSize = receiveBufferSize;
        }

        /// <summary>
        /// Frees any unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// An overrideable version of the Dispose.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Close();
            }
        }

        /// <summary>
        /// Gets the socket handle.
        /// </summary>
        /// <value>The socket handle.</value>
        public int Handle => m_webSocket?.GetHashCode() ?? -1;

        /// <summary>
        /// Gets the local endpoint.
        /// </summary>
        public EndPoint LocalEndpoint => m_localEndpoint;

        /// <summary>
        /// Gets the remote endpoint.
        /// </summary>
        public EndPoint RemoteEndpoint => m_remoteEndpoint;

        /// <summary>
        /// Gets the transport channel features implemented by this message socket.
        /// </summary>
        /// <value>The transport channel feature.</value>
        public TransportChannelFeatures MessageSocketFeatures =>
            TransportChannelFeatures.Reconnect;

        public async Task ConnectAsync(Uri endpointUrl, CancellationToken ct = default)
        {
            if (endpointUrl == null)
            {
                throw new ArgumentNullException(nameof(endpointUrl));
            }

            if (m_webSocket != null)
            {
                throw new InvalidOperationException("The WebSocket is already connected.");
            }

            ClientWebSocket clientWebSocket;
            try
            {
                clientWebSocket = await ConnectClientWebSocketAsync(endpointUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Failed to connect WebSocket to {EndpointUrl}", endpointUrl);
                throw;
            }

            m_webSocket = clientWebSocket;
            m_remoteEndpoint = new DnsEndPoint(endpointUrl.DnsSafeHost, endpointUrl.Port);
            m_localEndpoint = new DnsEndPoint("localhost", 0);

            m_logger.LogInformation("WebSocket connected to {EndpointUrl}", endpointUrl);
        }

        /// <summary>
        /// Connects to an endpoint. Used for the reverse-connect path (see
        /// <see cref="WebSocketServerChannel.BeginReverseConnect"/>); forward connects go
        /// through <see cref="ConnectAsync"/>.
        /// </summary>
        public bool BeginConnect(
            Uri endpointUrl,
            EventHandler<IMessageSocketAsyncEventArgs> callback,
            object state)
        {
            if (endpointUrl == null)
            {
                throw new ArgumentNullException(nameof(endpointUrl));
            }

            if (m_webSocket != null)
            {
                throw new InvalidOperationException("The WebSocket is already connected.");
            }

            Task.Run(async () =>
            {
                var eventArgs = new WebSocketMessageSocketAsyncEventArgs { UserToken = state };

                try
                {
                    m_webSocket = await ConnectClientWebSocketAsync(endpointUrl, CancellationToken.None)
                        .ConfigureAwait(false);
                    m_remoteEndpoint = new DnsEndPoint(endpointUrl.DnsSafeHost, endpointUrl.Port);
                    m_localEndpoint = new DnsEndPoint("localhost", 0);

                    eventArgs.IsSocketError = false;
                    m_logger.LogInformation("WebSocket connected to {EndpointUrl}", endpointUrl);
                }
                catch (Exception ex)
                {
                    m_logger.LogError(ex, "Failed to connect WebSocket to {EndpointUrl}", endpointUrl);
                    eventArgs.IsSocketError = true;
                    eventArgs.SocketErrorString = ex.Message;
                }

                callback?.Invoke(this, eventArgs);
            });

            return true;
        }

        /// <summary>
        /// Opens and connects a <see cref="ClientWebSocket"/> for <paramref name="endpointUrl"/>.
        /// </summary>
        private async Task<ClientWebSocket> ConnectClientWebSocketAsync(Uri endpointUrl, CancellationToken ct)
        {
            var clientWebSocket = new ClientWebSocket();

            // required by the server (WebSocketStartup.AcceptWebSocketAsync) - without it
            // the handshake fails with "... but server is only accepting 'opcua+uacp' protocol(s)".
            clientWebSocket.Options.AddSubProtocol(kOpcUaBinarySubProtocol);

            // opc.wss:// is not a scheme the OS TLS/WebSocket stack understands - use wss://.
            // Server TLS certificate validation is left to the default .NET/OS trust store;
            // IMessageSocketFactory.Create() is not given the app's ICertificateValidator, so
            // it cannot be wired in here without changing the SDK's factory contract.
            var wsUri = new UriBuilder(endpointUrl) { Scheme = "wss" }.Uri;

            m_logger.LogInformation("Connecting to WebSocket endpoint: {EndpointUrl}", wsUri);

            try
            {
                await clientWebSocket.ConnectAsync(wsUri, ct).ConfigureAwait(false);
            }
            catch
            {
                clientWebSocket.Dispose();
                throw;
            }

            return clientWebSocket;
        }

        /// <summary>
        /// Forcefully closes the socket.
        /// </summary>
        public void Close()
        {
            m_logger.LogInformation("Closing WebSocketMessageSocket");
            lock (m_socketLock)
            {
                m_closed = true;

                if (m_webSocket != null)
                {
                    try
                    {
                        if (m_webSocket.State == WebSocketState.Open)
                        {
                            m_webSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Closing",
                                CancellationToken.None).Wait(1000);
                        }
                    }
                    catch (Exception e)
                    {
                        m_logger.LogError(e, "Unexpected error closing WebSocket.");
                    }
                    finally
                    {
                        m_logger.LogInformation("Disposing WebSocket and setting it to null.");
                        m_webSocket.Dispose();
                        m_webSocket = null;
                    }
                }
            }
        }

        /// <summary>
        /// Starts reading messages from the socket.
        /// </summary>
        public void ReadNextMessage()
        {
            m_logger.LogInformation("Starting to read messages from WebSocket: {Closed}, State: {State}", m_closed, m_webSocket?.State);
            Task.Run(async () =>
            {
                m_logger.LogInformation("Starting WebSocket read loop");

                bool errorReported = false;

                while (!m_closed && m_webSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        if (!await ReadNextMessageAsync().ConfigureAwait(false))
                        {
                            // a close frame was received and already reported to the sink.
                            errorReported = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        m_logger.LogError(ex, "Error reading WebSocket message - Type: {ExceptionType}, Message: {Message}, Stack: {StackTrace}",
                            ex.GetType().Name, ex.Message, ex.StackTrace);
                        m_sink?.OnReceiveError(this, ServiceResult.Create(ex, StatusCodes.BadTcpInternalError, ex.Message));
                        errorReported = true;
                        break;
                    }
                }

                // The loop can also end without an exception or a close frame - e.g. an abrupt
                // reset transitions the WebSocket straight to Aborted. Report that too, unless
                // this is an intentional local Close().
                if (!m_closed && !errorReported)
                {
                    m_logger.LogInformation("WebSocket connection lost - State: {State}", m_webSocket?.State);
                    m_sink?.OnReceiveError(this, ServiceResult.Create(
                        StatusCodes.BadConnectionClosed,
                        "WebSocket connection was lost."));
                }

                m_logger.LogInformation("WebSocket read loop ended - Closed: {Closed}, State: {State}", m_closed, m_webSocket?.State);
            });
        }

        /// <summary>
        /// Reads the next message from the WebSocket.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> if a close frame was received (already reported to the
        /// sink) and the read loop should stop; <see langword="true"/> to keep reading.
        /// </returns>
        private async Task<bool> ReadNextMessageAsync()
        {
            byte[] buffer = m_bufferManager.TakeBuffer(m_receiveBufferSize, "ReadNextMessageAsync");

            try
            {
                m_logger.LogDebug("Waiting to receive WebSocket message...");
                // Reserve the last byte: BufferManager stores its lock cookie there.
                var result = await m_webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, 0, buffer.Length - 1),
                    CancellationToken.None).ConfigureAwait(false);

                m_logger.LogDebug("Received WebSocket frame - Type: {MessageType}, Count: {Count}, EndOfMessage: {EndOfMessage}",
                    result.MessageType, result.Count, result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    m_logger.LogInformation("WebSocket close frame received - CloseStatus: {CloseStatus}, CloseDescription: {CloseDescription}",
                        result.CloseStatus, result.CloseStatusDescription);
                    m_bufferManager.ReturnBuffer(buffer, "ReadNextMessageAsync");
                    buffer = null;
                    m_sink?.OnReceiveError(this, ServiceResult.Create(
                        StatusCodes.BadConnectionClosed,
                        "WebSocket closed by remote endpoint"));
                    return false;
                }

                int count = result.Count;

                if (!result.EndOfMessage)
                {
                    // Slow path: the message is fragmented across frames - reassemble before delivering it.
                    using var stream = new MemoryStream();
                    stream.Write(buffer, 0, count);
                    m_bufferManager.ReturnBuffer(buffer, "ReadNextMessageAsync");
                    buffer = null;

                    while (!result.EndOfMessage)
                    {
                        byte[] continuation = m_bufferManager.TakeBuffer(m_receiveBufferSize, "ReadNextMessageAsync");
                        try
                        {
                            result = await m_webSocket.ReceiveAsync(
                                new ArraySegment<byte>(continuation, 0, continuation.Length - 1),
                                CancellationToken.None).ConfigureAwait(false);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                m_logger.LogInformation("WebSocket close frame received while reassembling a message - CloseStatus: {CloseStatus}, CloseDescription: {CloseDescription}",
                                    result.CloseStatus, result.CloseStatusDescription);
                                m_sink?.OnReceiveError(this, ServiceResult.Create(
                                    StatusCodes.BadConnectionClosed,
                                    "WebSocket closed by remote endpoint"));
                                return false;
                            }

                            stream.Write(continuation, 0, result.Count);
                        }
                        finally
                        {
                            m_bufferManager.ReturnBuffer(continuation, "ReadNextMessageAsync");
                        }
                    }

                    count = (int)stream.Length;
                    buffer = m_bufferManager.TakeBuffer(count, "ReadNextMessageAsync");
                    Buffer.BlockCopy(stream.GetBuffer(), 0, buffer, 0, count);

                    m_logger.LogDebug("Reassembled fragmented WebSocket message into {MessageSize} bytes", count);
                }

                if (count > 0)
                {
                    m_logger.LogDebug("Received WebSocket message of {MessageSize} bytes", count);

                    if (m_sink != null)
                    {
                        // Pass the buffer to the sink - it will return it to the buffer manager
                        var messageChunk = new ArraySegment<byte>(buffer, 0, count);

                        // Log first few bytes for debugging
                        var preview = string.Join(" ", messageChunk.Array.Take(Math.Min(16, count)).Select(b => b.ToString("X2")));
                        m_logger.LogDebug("Message bytes (first 16): {Preview}", preview);

                        // Ownership transfers to the sink here, even if OnMessageReceived throws.
                        buffer = null;
                        m_sink.OnMessageReceived(this, messageChunk);
                    }
                    else
                    {
                        m_logger.LogWarning("Received WebSocket message but sink is null, discarding {MessageSize} bytes", count);
                        m_bufferManager.ReturnBuffer(buffer, "ReadNextMessageAsync");
                        buffer = null;
                    }
                }
                else
                {
                    m_bufferManager.ReturnBuffer(buffer, "ReadNextMessageAsync");
                    buffer = null;
                }

                return true;
            }
            catch
            {
                // do not swallow: the caller's catch reports the failure to the sink.
                if (buffer != null)
                {
                    m_bufferManager.ReturnBuffer(buffer, "ReadNextMessageAsync");
                }
                throw;
            }
        }

        /// <summary>
        /// Changes the sink used to report reads.
        /// </summary>
        public void ChangeSink(IMessageSink sink)
        {
            lock (m_socketLock)
            {
                m_sink = sink;
            }
        }

        /// <summary>
        /// Sends a buffer.
        /// </summary>
        public bool Send(IMessageSocketAsyncEventArgs args)
        {
            m_logger.LogDebug("Send called to send WebSocket message");
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            Task.Run(async () =>
            {
                var wsArgs = args as WebSocketMessageSocketAsyncEventArgs;

                try
                {
                    if (m_webSocket?.State == WebSocketState.Open)
                    {
                        byte[] buffer = wsArgs.Buffer;
                        int offset = wsArgs.Offset;
                        int count = wsArgs.Count;

                        if (wsArgs.BufferList != null)
                        {
                            // Combine buffer list into single buffer
                            int totalSize = 0;
                            foreach (var buf in wsArgs.BufferList)
                            {
                                totalSize += buf.Count;
                            }

                            buffer = new byte[totalSize];
                            int position = 0;
                            foreach (var buf in wsArgs.BufferList)
                            {
                                System.Buffer.BlockCopy(buf.Array, buf.Offset, buffer, position, buf.Count);
                                position += buf.Count;
                            }
                            offset = 0;
                            count = totalSize;
                        }

                        m_logger.LogDebug("Sending WebSocket message of {MessageSize} bytes, State: {State}", count, m_webSocket.State);
                        await m_webSocket.SendAsync(
                            new ArraySegment<byte>(buffer, offset, count),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None).ConfigureAwait(false);

                        m_logger.LogDebug("WebSocket message sent successfully");
                        wsArgs.BytesTransferred = count;
                        wsArgs.IsSocketError = false;
                    }
                    else
                    {
                        wsArgs.IsSocketError = true;
                        wsArgs.SocketErrorString = "WebSocket not connected";
                    }
                }
                catch (Exception ex)
                {
                    m_logger.LogError(ex, "Error sending WebSocket message");
                    wsArgs.IsSocketError = true;
                    wsArgs.SocketErrorString = ex.Message;
                }

                wsArgs.OnCompleted();
            });

            return true;
        }

        /// <summary>
        /// Get the message socket event args.
        /// </summary>
        public IMessageSocketAsyncEventArgs MessageSocketEventArgs()
        {
            return new WebSocketMessageSocketAsyncEventArgs();
        }

        private readonly ILogger m_logger;
        private IMessageSink m_sink;
        private WebSocket m_webSocket;
        private readonly BufferManager m_bufferManager;
        private readonly int m_receiveBufferSize;
        private bool m_closed;
        private readonly object m_socketLock = new object();
        private EndPoint m_localEndpoint;
        private EndPoint m_remoteEndpoint;

        private const string kOpcUaBinarySubProtocol = "opcua+uacp";
    }
}
