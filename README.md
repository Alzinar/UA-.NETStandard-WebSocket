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

**Note on WSS certificate trust:** the TLS handshake is validated by the OPC UA
`CertificateValidator` of the application, on both sides. The server presents its application
instance certificate and checks the client's TLS certificate (if sent) against its trust lists; the
client checks the server's certificate against its own trust lists. `AutoAcceptUntrustedCertificates`
and the `CertificateValidation` event therefore apply to WSS exactly as for `opc.tcp`, and no OS
trust store configuration is needed. .NET/OS chain and host name errors are ignored in favor of the
OPC UA validator's decision.

## AI Assistance
This project was developed with the assistance of artificial intelligence (AI) tools. AI-generated code, suggestions, and documentation were reviewed, modified, and validated by the maintainers before inclusion in this repository.

The maintainers remain responsible for the design, implementation, security, and correctness of the software.