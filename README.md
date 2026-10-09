# UA-.NETStandard
A websocket implemenation for UA-.NETStandard

## Integrating into your application
The WebSocket transport is opt-in: register the listener/channel factory for the `opc.wss` URI
scheme before starting your server or client, then use an `opc.wss://` endpoint URL like any other
OPC UA transport.

### Server

```csharp
using Opc.Ua.Bindings;

// register the WebSocket transport listener before starting the server
TransportBindings.Listeners.SetBinding(new WebSocketTransportListenerFactory());

// add an "opc.wss://" base address alongside (or instead of) "opc.tcp://"
configuration.ServerConfiguration.BaseAddresses.Add("opc.wss://localhost:4843/UaServer");
```

### Client

```csharp
using Opc.Ua.Bindings;

// register the WebSocket transport channel before connecting
((ITransportBindings<ITransportChannelFactory>)TransportBindings.Channels)
    .SetBinding(new WebSocketTransportChannelFactory());

// then connect as usual to an "opc.wss://" endpoint
```

**Note on WSS certificate trust:** the WebSocket transport's TLS handshake is validated by the
underlying .NET/OS certificate trust store, not by the OPC UA `CertificateValidator`. When
connecting to a server with a self-signed certificate (e.g. for local testing), the client process
must be configured to trust that certificate at the OS/.NET level — `AutoAcceptUntrustedCertificates`
alone is not sufficient for the WSS transport layer. In production, use a certificate issued by a
CA the client already trusts to avoid this entirely.

## AI Assistance
This project was developed with the assistance of artificial intelligence (AI) tools. AI-generated code, suggestions, and documentation were reviewed, modified, and validated by the maintainers before inclusion in this repository.

The maintainers remain responsible for the design, implementation, security, and correctness of the software.