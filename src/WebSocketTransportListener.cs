using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua.Security.Certificates;

namespace Opc.Ua.Bindings
{

    /// <summary>
    /// Manages the connections for a UA WebSocket server.
    /// </summary>
    public class WebSocketTransportListener : ITransportListener, ITcpChannelListener
    {
        /// <summary>
        /// Raised when a new connection is waiting for a client.
        /// </summary>
        public event ConnectionWaitingHandlerAsync ConnectionWaiting;

        /// <summary>
        /// Raised when a monitored connection's status changed.
        /// </summary>
        public event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;

        private readonly ILogger m_logger;
        private readonly ITelemetryContext m_telemetry;
        private IHost m_host;
        private BufferManager m_bufferManager;
        private CertificateTypesProvider m_serverCertificateTypesProvider;
        private ChannelQuotas m_quotas;
        private ITransportListenerCallback m_callback;
        private ConcurrentDictionary<uint, WebSocketListenerChannel> m_channels;
        private uint m_lastChannelId;
        private readonly object m_lock = new object();
        private EndpointDescriptionCollection m_descriptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketTransportListener"/> class.
        /// </summary>
        public WebSocketTransportListener(ITelemetryContext telemetry)
        {
            m_logger = telemetry.CreateLogger<WebSocketTransportListener>();
            m_telemetry = telemetry;
            ListenerId = Guid.NewGuid().ToString();
            m_channels = new ConcurrentDictionary<uint, WebSocketListenerChannel>();
        }

        /// <inheritdoc/>
        public string ListenerId { get; }

        /// <inheritdoc/>
        public string UriScheme => Utils.UriSchemeOpcWss;

        /// <inheritdoc/>
        public Uri EndpointUrl { get; private set; }

        /// <summary>
        /// The maximum number of secure channels
        /// </summary>
        public int MaxChannelCount { get; private set; }


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
                lock (m_lock)
                {
                    m_host?.Dispose();
                    m_host = null;

                    foreach (var channel in m_channels.Values)
                    {
                        Utils.SilentDispose(channel);
                    }

                    m_channels.Clear();
                }
            }
        }

        /// <summary>
        /// Opens the listener and starts accepting connections.
        /// </summary>
        public void Open(
            Uri baseAddress,
            TransportListenerSettings settings,
            ITransportListenerCallback callback)
        {
            m_logger.LogInformation("Opening WebSocket listener on {EndpointUrl}", baseAddress);
            m_bufferManager = new BufferManager(
                "Server",
                Math.Max(settings.Configuration.MaxBufferSize, settings.Configuration.MaxMessageSize), m_telemetry);
            m_descriptions = settings.Descriptions;
            m_serverCertificateTypesProvider = settings.ServerCertificateTypesProvider;

            EndpointUrl = baseAddress;
            EndpointConfiguration configuration = settings.Configuration;
            m_quotas = new ChannelQuotas(new ServiceMessageContext(m_telemetry)
            {
                MaxArrayLength = configuration.MaxArrayLength,
                MaxByteStringLength = configuration.MaxByteStringLength,
                MaxMessageSize = configuration.MaxMessageSize,
                MaxStringLength = configuration.MaxStringLength,
                MaxEncodingNestingLevels = configuration.MaxEncodingNestingLevels,
                MaxDecoderRecoveries = configuration.MaxDecoderRecoveries,
                NamespaceUris = settings.NamespaceUris,
                ServerUris = new StringTable(),
                Factory = settings.Factory
            })
            {
                MaxBufferSize = configuration.MaxBufferSize,
                MaxMessageSize = configuration.MaxMessageSize,
                ChannelLifetime = configuration.ChannelLifetime,
                SecurityTokenLifetime = configuration.SecurityTokenLifetime,
                CertificateValidator = settings.CertificateValidator
            };
            MaxChannelCount = settings.MaxChannelCount;

            // save the callback to the server.
            m_callback = callback;
            // EndpointUrl = baseAddress;
            // m_callback = callback;
            // m_serverCertificateTypesProvider = settings.ServerCertificateTypesProvider;
            // m_endpoints = settings.Descriptions;

            Start();
        }

        /// <inheritdoc/>
        public void CertificateUpdate(
            ICertificateValidator validator,
            CertificateTypesProvider certificateTypesProvider)
        {
            m_quotas.CertificateValidator = validator;
            m_serverCertificateTypesProvider = certificateTypesProvider;
            foreach (EndpointDescription description in m_descriptions)
            {
                // TODO: why only if SERVERCERT != null
                if (description.ServerCertificate != null)
                {
                    X509Certificate2 serverCertificate = certificateTypesProvider
                        .GetInstanceCertificate(
                            description.SecurityPolicyUri);
                    if (certificateTypesProvider.SendCertificateChain)
                    {
                        description.ServerCertificate = certificateTypesProvider
                            .LoadCertificateChainRaw(
                                serverCertificate);
                    }
                    else
                    {
                        description.ServerCertificate = serverCertificate.RawData;
                    }
                }
            }
        }

        /// <summary>
        /// Closes the listener and stops accepting connections.
        /// </summary>
        public void Close()
        {
            m_logger.LogInformation("Closing WebSocket listener");
            Stop();
        }

        /// <summary>
        /// Starts listening at the specified port.
        /// </summary>
        public void Start()
        {
            WebSocketStartup.Listener = this;

            // opc.wss is the only supported scheme and is always TLS-secured per the OPC UA spec;
            // fail fast instead of silently falling back to an unencrypted endpoint.
            bool requireTls = string.Equals(
                EndpointUrl.Scheme,
                Utils.UriSchemeOpcWss,
                StringComparison.OrdinalIgnoreCase);

            X509Certificate2 serverCertificate = null;
            if (requireTls)
            {
                serverCertificate = m_serverCertificateTypesProvider?.GetInstanceCertificate(
                    SecurityPolicies.Basic256Sha256);

                if (serverCertificate == null)
                {
                    throw new ServiceResultException(
                        StatusCodes.BadConfigurationError,
                        Utils.Format(
                            "No server certificate is configured for the TLS-secured WebSocket endpoint {0}.",
                            EndpointUrl));
                }
            }

            UriHostNameType hostType = Uri.CheckHostName(EndpointUrl.Host);
            IPAddress ipAddress = hostType is UriHostNameType.Dns or UriHostNameType.Unknown or UriHostNameType.Basic
                ? null
                : IPAddress.Parse(EndpointUrl.Host);

            IHostBuilder hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel(options =>
                    {
                        void ConfigureListenOptions(ListenOptions listenOptions)
                        {
                            if (requireTls)
                            {
                                listenOptions.UseHttps(serverCertificate, httpsOptions =>
                                {
                                    // request (not require) a TLS client certificate and check it
                                    // against the configured OPC UA certificate trust list.
                                    httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                                    httpsOptions.ClientCertificateValidation = ValidateClientCertificate;
                                });
                            }
                        }

                        if (ipAddress == null)
                        {
                            // bind to any address
                            options.ListenAnyIP(EndpointUrl.Port, ConfigureListenOptions);
                        }
                        else
                        {
                            // bind to specific address
                            options.Listen(ipAddress, EndpointUrl.Port, ConfigureListenOptions);
                        }
                    });
                    webBuilder.UseContentRoot(Directory.GetCurrentDirectory());
                    webBuilder.UseUrls(Utils.ReplaceLocalhost(EndpointUrl.ToString()));
                    webBuilder.UseStartup<WebSocketStartup>();
                });

            m_host = hostBuilder.Build();
            m_host.Start();

            m_logger.LogInformation(
                "WebSocket listener started on {EndpointUrl} (TLS: {RequireTls})",
                EndpointUrl,
                requireTls);
        }

        /// <summary>
        /// Validates a TLS client certificate against the configured OPC UA certificate trust list.
        /// </summary>
        private bool ValidateClientCertificate(X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
        {
            ICertificateValidator validator = m_quotas?.CertificateValidator;
            if (validator == null)
            {
                // no OPC UA validator configured - fall back to the TLS chain result.
                return errors == SslPolicyErrors.None;
            }

            try
            {
                validator.ValidateAsync(certificate, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                m_logger.LogWarning(ex, "Rejected TLS client certificate {Subject}.", certificate?.Subject);
                return false;
            }
        }

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void Stop()
        {
            Dispose();
        }

        /// <inheritdoc/>
        public void CreateReverseConnection(Uri url, int timeout)
        {
            ConnectionWaiting = null;
            ConnectionStatusChanged = null;
            m_logger.LogInformation("Creating reverse connection to {Url} with timeout {Timeout}", url, timeout);
            // Suppress warnings
            ConnectionWaiting = null;
            ConnectionWaiting?.Invoke(null, null);
            ConnectionStatusChanged = null;
            ConnectionStatusChanged?.Invoke(null, null);
            throw new NotImplementedException("Reverse connect not implemented for WebSocket transport.");
        }

        /// <inheritdoc/>
        public void UpdateChannelLastActiveTime(string globalChannelId)
        {
            m_logger.LogDebug("Updating last active time for channel {GlobalChannelId}", globalChannelId);
            // intentionally not implemented
        }

        /// <summary>
        /// Handles a new WebSocket connection.
        /// </summary>
        public async Task HandleWebSocketConnectionAsync(HttpContext context, System.Net.WebSockets.WebSocket webSocket)
        {
            m_logger.LogInformation("Handling new WebSocket connection from {RemoteIpAddress}", context.Connection.RemoteIpAddress);

            WebSocketListenerChannel channel = null;
            System.Net.WebSockets.WebSocket activeWebSocket = null;

            lock (m_lock)
            {
                ConcurrentDictionary<uint, WebSocketListenerChannel> channels = m_channels;
                if (channels != null)
                {
                    // TODO: .Count is flagged as hotpath, implement separate counter
                    int channelCount = channels.Count;

                    // Remove oldest channel that does not have a session attached to it
                    // before reaching m_maxChannelCount
                    if (MaxChannelCount > 0 && MaxChannelCount == channelCount)
                    {
                        KeyValuePair<uint, WebSocketListenerChannel>[] snapshot = [.. channels];

                        // Identify channels without established sessions
                        KeyValuePair<uint, WebSocketListenerChannel>[] nonSessionChannels =
                        [
                            .. snapshot.Where(ch => !ch.Value.UsedBySession)
                        ];

                        if (nonSessionChannels.Length != 0)
                        {
                            KeyValuePair<uint, WebSocketListenerChannel> oldestIdChannel
                                = nonSessionChannels.Aggregate(
                                (max, current) =>
                                    current.Value.ElapsedSinceLastActiveTime > max.Value
                                        .ElapsedSinceLastActiveTime
                                        ? current
                                        : max);

                            m_logger.LogInformation(
                                "TCPLISTENER: Channel Id {Id} scheduled for IdleCleanup - Oldest without established session.",
                                oldestIdChannel.Value.Id);
                            oldestIdChannel.Value.IdleCleanup();
                            m_logger.LogInformation(
                                "TCPLISTENER: Channel Id {Id} finished IdleCleanup - Oldest without established session.",
                                oldestIdChannel.Value.Id);

                            channelCount--;
                        }
                    }

                    bool serveChannel = !(MaxChannelCount > 0 &&
                        MaxChannelCount < channelCount);
                    if (!serveChannel)
                    {
                        m_logger.LogError(
                            "OnAccept: Maximum number of channels {CurrentCount} reached, serving channels is stopped until number is lower or equal than {MaxChannelCount} ",
                            channelCount,
                            MaxChannelCount);

                        // reject the connection instead of letting it through unbounded.
                        webSocket.Abort();
                    }
                    else
                    {
                        channel = null;
                        try
                        {

                            channel = new WebSocketServerChannel(
                                    ListenerId,
                                    this,
                                    m_bufferManager,
                                    m_quotas,
                                    m_serverCertificateTypesProvider,
                                    m_descriptions,
                                    m_telemetry);

                            if (m_callback != null)
                            {
                                channel.SetRequestReceivedCallback(
                                    new WebSocketChannelRequestEventHandler(OnRequestReceivedAsync));
                                channel.SetReportOpenSecureChannelAuditCallback(
                                    new WebSocketReportAuditOpenSecureChannelEventHandler(
                                        OnReportAuditOpenSecureChannelEvent));
                                channel.SetReportCloseSecureChannelAuditCallback(
                                    new WebSocketReportAuditCloseSecureChannelEventHandler(
                                        OnReportAuditCloseSecureChannelEvent));
                                channel.SetReportCertificateAuditCallback(
                                    new WebSocketReportAuditCertificateEventHandler(
                                        OnReportAuditCertificateEvent));
                            }

                            uint channelId;
                            do
                            {
                                // get channel id
                                channelId = GetNextChannelId();

                                // save the channel for shutdown and reconnects.
                                // retry to get a channel id if it is already in use.
                            } while (!channels.TryAdd(channelId, channel));

                            // start accepting messages on the channel.
                            channel.Attach(channelId, webSocket);

                            // Keep reference to the active WebSocket to wait outside the lock
                            activeWebSocket = webSocket;

                            channel = null;
                        }
                        catch (Exception ex)
                        {
                            m_logger.LogError(ex, "Unexpected error accepting a new connection.");
                        }
                        finally
                        {
                            Utils.SilentDispose(channel);
                        }
                    }
                }
            }

            // Keep the HTTP context alive while the WebSocket is open (outside the lock)
            if (activeWebSocket != null)
            {
                while (activeWebSocket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                m_logger.LogInformation("WebSocket connection closed, State: {State}", activeWebSocket.State);
            }
        }
        /// <summary>
        /// Handles requests arriving from a channel.
        /// </summary>
        private async void OnRequestReceivedAsync(
            WebSocketListenerChannel channel,
            uint requestId,
            IServiceRequest request)
        {
            try
            {
                if (m_callback != null)
                {
                    var context = new SecureChannelContext(
                        channel.GlobalChannelId,
                        channel.EndpointDescription,
                        RequestEncoding.Binary);

                    IServiceResponse response = await m_callback.ProcessRequestAsync(
                        context,
                        request).ConfigureAwait(false);

                    try
                    {
                        ((WebSocketServerChannel)channel).SendResponse(requestId, response);
                    }
                    catch (ServiceResultException sre) when (sre.StatusCode == StatusCodes.BadSecureChannelClosed)
                    {
                        // try to find the new channel id for the authentication token to send response over new channel
                        NodeId authenticationToken = request.RequestHeader.AuthenticationToken;
                        if (m_callback.TryGetSecureChannelIdForAuthenticationToken(
                                authenticationToken,
                                out uint channelId
                            ) &&
                            m_channels.TryGetValue(channelId, out WebSocketListenerChannel newChannel))
                        {
                            var serverChannel = (WebSocketServerChannel)newChannel;

                            // if the channel is not the same as the one we started with, send the response over the new channel
                            if (serverChannel != channel)
                            {
                                serverChannel.SendResponse(requestId, response);
                                return;
                            }
                        }
                        // if we could not find a new channel, just log the error
                        throw;
                    }
                }
            }
            catch (Exception e)
            {
                m_logger.LogError(e, "TCPLISTENER - Unexpected error processing request.");
            }
        }

        /// <summary>
        /// Callback for reporting the open secure channel audit event
        /// </summary>
        private void OnReportAuditOpenSecureChannelEvent(
            WebSocketServerChannel channel,
            OpenSecureChannelRequest request,
            X509Certificate2 clientCertificate,
            Exception exception)
        {
            try
            {
                m_callback?.ReportAuditOpenSecureChannelEvent(
                    channel.GlobalChannelId,
                    channel.EndpointDescription,
                    request,
                    clientCertificate,
                    exception);
            }
            catch (Exception e)
            {
                m_logger.LogError(
                    e,
                    "TCPLISTENER - Unexpected error sending OpenSecureChannel Audit event.");
            }
        }

        /// <summary>
        /// Callback for reporting the close secure channel audit event
        /// </summary>
        private void OnReportAuditCloseSecureChannelEvent(
            WebSocketServerChannel channel,
            Exception exception)
        {
            try
            {
                m_callback?.ReportAuditCloseSecureChannelEvent(channel.GlobalChannelId, exception);
            }
            catch (Exception e)
            {
                m_logger.LogError(
                    e,
                    "TCPLISTENER - Unexpected error sending CloseSecureChannel Audit event.");
            }
        }

        /// <summary>
        /// Callback for reporting the certificate audit events
        /// </summary>
        private void OnReportAuditCertificateEvent(
            X509Certificate2 clientCertificate,
            Exception exception)
        {
            try
            {
                m_callback?.ReportAuditCertificateEvent(clientCertificate, exception);
            }
            catch (Exception e)
            {
                m_logger.LogError(
                    e,
                    "TCPLISTENER - Unexpected error sending Certificate Audit event.");
            }
        }

        /// <summary>
        /// Gets the next available channel ID.
        /// </summary>
        private uint GetNextChannelId()
        {
            lock (m_lock)
            {
                return ++m_lastChannelId;
            }
        }

        /// <inheritdoc/>
        public bool ReconnectToExistingChannel(IMessageSocket socket, uint requestId, uint sequenceNumber, uint channelId,
        X509Certificate2 clientCertificate, ChannelToken token, OpenSecureChannelRequest request)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public async Task<bool> TransferListenerChannel(uint channelId, string serverUri, Uri endpointUrl)
        {
            bool accepted = false;

            // remove it so it does not get cleaned up as an inactive connection.
            if (m_channels?.TryRemove(channelId, out WebSocketListenerChannel channel) != true)
            {
                throw ServiceResultException.Create(
                    StatusCodes.BadTcpSecureChannelUnknown,
                    "Could not find secure channel request.");
            }

            // notify the application.
            if (ConnectionWaiting != null)
            {
                var args = new WebSocketConnectionWaitingEventArgs(
                    serverUri,
                    endpointUrl,
                    channel.GetSocket());
                await ConnectionWaiting(this, args).ConfigureAwait(false);
                accepted = args.Accepted;
            }

            if (!accepted)
            {
                // add back in for other connection attempt.
                m_channels?.TryAdd(channelId, channel);
            }

            return accepted;
        }

        /// <inheritdoc/>
        public Task<bool> TransferListenerChannelAsync(uint channelId, string serverUri, Uri endpointUrl)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public void ChannelClosed(uint channelId)
        {
            if (m_channels?.TryRemove(channelId, out WebSocketListenerChannel channel) == true)
            {
                Utils.SilentDispose(channel);
                m_logger.LogInformation("ChannelId {Id}: closed", channelId);
            }
            else
            {
                m_logger.LogInformation("ChannelId {Id}: closed, but channel was not found", channelId);
            }
        }
    }

    /// <summary>
    /// The Tcp specific arguments passed to the ConnectionWaiting event.
    /// </summary>
    public class WebSocketConnectionWaitingEventArgs : ConnectionWaitingEventArgs
    {
        internal WebSocketConnectionWaitingEventArgs(
            string serverUrl,
            Uri endpointUrl,
            IMessageSocket socket)
            : base(serverUrl, endpointUrl)
        {
            Socket = socket;
        }

        /// <inheritdoc/>
        public override object Handle => Socket;

        /// <inheritdoc/>
        internal IMessageSocket Socket { get; }
    }
}
