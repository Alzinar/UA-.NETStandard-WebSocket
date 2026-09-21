# TODO — WebSocket transport fixes

Working, opcjs-tested server code is in `tmp/OpcServer/WebSockets/`. It is the same
11 files as `src/`, renamed into another namespace (`m_x` → `_x` field naming, nullable
pragmas added). After stripping naming/formatting noise, only **three** files carry real
behavioural differences — those are the verified fixes in Phase 1.

Reference for the original TCP transport:
<https://github.com/OPCFoundation/UA-.NETStandard> (`Stack/Opc.Ua.Core/Stack/Tcp/`).

Legend: `[ ]` open · `[x]` done

---

## Phase 1 — Port verified fixes from the working server

These are confirmed differences against code that interoperates with
[opcjs](https://github.com/iarbre/opcjs). Highest value, lowest risk. Do these first.

- [x] **1.1 — `BufferManager` cookie byte corruption (server-breaking)**
  `src/WebSocketMessageSocket.cs` · `ReadNextMessageAsync`
  We pass `new ArraySegment<byte>(buffer)` — the whole array — to `ReceiveAsync`.
  `BufferManager` keeps a lock cookie in the **last byte** of the rented array, so a frame
  that fills the buffer overwrites it and `ReturnBuffer` throws
  `InvalidOperationException: Buffer has been locked.`
  Verified with the real `BufferManager` (max 8192 → array length 16384, cookie `b[^1] == 0x5A`):

  | write range | `ReturnBuffer` |
  |---|---|
  | whole array (ours) | **throws** `Buffer has been locked.` |
  | `Length - 1` (reference) | succeeds |

  Fix: reserve the final byte — `new ArraySegment<byte>(buffer, 0, buffer.Length - 1)` —
  in the initial read *and* every continuation read.

- [x] **1.2 — Reassemble fragmented WebSocket messages**
  `src/WebSocketMessageSocket.cs` · `ReadNextMessageAsync`
  We ignore `result.EndOfMessage` and hand each frame straight to the sink, so a UA message
  split across frames is delivered as fragments. Verified: a 40-byte chunk sent as two
  frames arrives as `count=16 eom=False` then `count=24` whose first 3 header bytes are
  garbage — the decoder sees a corrupt message.
  Fix: adopt the reference's fast path / slow path:
  - fast path — `result.EndOfMessage` true → deliver directly (today's behaviour);
  - slow path — accumulate continuation frames into a `MemoryStream`, then `TakeBuffer(totalBytes)`
    and deliver one complete buffer.

- [x] **1.3 — Buffer double-return / ownership**
  `src/WebSocketMessageSocket.cs` · `ReadNextMessageAsync`
  Once `OnMessageReceived` is called the sink owns the buffer, but our outer `catch` returns
  it again if the sink throws. The reference sets `buffer = null` at every ownership transfer
  and guards the catch with `if (buffer != null)`.
  Fix: null out the local on hand-off (close-frame path too) and null-check in the catch.

- [x] **1.4 — Size the `BufferManager` for `MaxMessageSize`**
  `src/WebSocketTransportListener.cs` · `Open`
  Ours: `new BufferManager("Server", settings.Configuration.MaxBufferSize, m_telemetry)`.
  Reference: `Math.Max(settings.Configuration.MaxBufferSize, settings.Configuration.MaxMessageSize)`.
  Required by 1.2 — reassembly calls `TakeBuffer(totalBytes)` with a full message size, which
  can exceed `MaxBufferSize` and would otherwise throw.

- [x] **1.5 — Dispose `ArraySegmentStream` in the server channel**
  `src/WebSocketServerChannel.cs` (3 sites: open / close / request decode)
  We pass `new ArraySegmentStream(chunksToProcess)` inline to `BinaryDecoder.DecodeMessage`
  and never dispose it. Reference hoists it to `using var stream = …`.
  Fix: apply `using var` at all three call sites.

- [x] **1.6 — ~~Dispose the `ChannelToken` in `ProcessRequestMessage`~~ (invalid — do not implement)**
  `src/WebSocketServerChannel.cs` · `ProcessRequestMessage` `finally`
  Investigated against the SDK source for the exact `Opc.Ua.Core` commit this project
  depends on (`511ef72a…`, package `1.5.378.176`): `ReadSymmetricMessage` returns
  `CurrentToken`/`PreviousToken` by reference — a live, shared token still needed to sign/
  encrypt subsequent responses and decrypt subsequent requests on the same channel. The
  SDK's own `TcpServerChannel.ProcessRequestMessage` finally only does
  `chunksToProcess?.Release(...)`; it never disposes `token` there. The `token.Dispose()` +
  CA2000-suppression pattern only exists in `ProcessOpenSecureChannelRequest`, where `token`
  is a *newly created* token (`CreateToken()`) that must be disposed unless ownership is
  explicitly transferred (`token = null` after `ActivateToken`/`SetRenewedToken`) — and
  `WebSocketServerChannel.ProcessOpenSecureChannelRequest` already has that exact pattern.
  Adding `token.Dispose()` to `ProcessRequestMessage` would dispose the live channel token
  and break the channel after the first request. Not implemented.

- [ ] **1.7 — Build & smoke-test against opcjs**
  Confirm 0 warnings, then run the opcjs client end-to-end: connect → `GetEndpoints` →
  `OpenSecureChannel` → `CreateSession` → `ActivateSession` → `Read`. This validates
  Phase 1 before moving on.

---

## Phase 2 — Server bugs still present in *both* implementations

The working server has these too — they are latent (opcjs did not happen to trigger them),
not fixed. Verified independently against the SDK's TCP transport.

- [ ] **2.1 — Connection loss is never reported to the channel**
  `src/WebSocketMessageSocket.cs`
  `ReadNextMessageAsync`'s catch logs and swallows, so the outer `catch` that calls
  `OnReceiveError` never runs. Verified on an abrupt disconnect (no close handshake):
  `ws.State=Aborted, OnReceiveError calls=0` — the read loop just exits.
  Consequence: `HandleSocketError` / `ForceChannelFault` / `ChannelClosed` never fire, so
  the channel and its buffers **leak on every ungraceful disconnect** and the listener keeps
  a dead entry in `m_channels`.
  Fix: rethrow (or report `OnReceiveError` directly) for non-cancellation exceptions, and
  report an error when the loop exits with the socket no longer `Open`.

- [ ] **2.2 — `MaxChannelCount` is computed but never enforced**
  `src/WebSocketTransportListener.cs` · `HandleWebSocketConnectionAsync`
  ```csharp
  bool serveChannel = !(MaxChannelCount > 0 && MaxChannelCount < channelCount);
  if (!serveChannel) { m_logger.LogError(...); }   // logged, then ignored
  ```
  The SDK's `TcpTransportListener.OnAccept` gates creation on this flag and disposes the
  socket. We create the channel anyway → unbounded channel growth (DoS).
  Fix: when `!serveChannel`, abort the WebSocket and return without creating a channel.
  Also drop the hardcoded `bool isBlocked = false` or wire it to a real check.

- [ ] **2.3 — Chunk ordering can be violated on send**
  `src/WebSocketMessageSocket.cs` · `Send`
  Every call dispatches its own `Task.Run`, so chunks race. Verified — dispatching chunks
  0…15 in order produced `2,0,3,1,15,4,6,7,9,14,8,10,12,5,11,13`.
  Harmless for single-chunk messages (likely why opcjs passed), fatal for any message that
  spans chunks and for sequence-number validation.
  Fix: serialize sends through a `SemaphoreSlim(1,1)` or a single-consumer send queue.
  (`WebSocket.SendAsync` also forbids overlapping sends.)

- [ ] **2.4 — `UpdateChannelLastActiveTime` is a no-op + no inactivity timer**
  `src/WebSocketTransportListener.cs`
  The SDK resolves the channel from `globalChannelId` and calls `UpdateLastActiveTime()`, and
  runs a `DetectInactiveChannels` timer against `Quotas.ChannelLifetime`.
  We do neither, so `ChannelLifetime` is unenforced and stale channels are never reaped
  (spec Part 6 requires channel lifetime enforcement). `ElapsedSinceLastActiveTime` already
  exists on `WebSocketListenerChannel` and is used by the `MaxChannelCount` eviction path,
  so that eviction is currently driven by stale data.
  Fix: implement the lookup, and add the periodic cleanup timer.

- [ ] **2.5 — `OnRequestReceivedAsync` is `async void`**
  `src/WebSocketTransportListener.cs`
  An exception escaping the handler crashes the process instead of faulting the channel.
  Fix: return `Task` and observe it, or wrap the whole body defensively.

- [ ] **2.6 — Replace the per-connection 100 ms polling keep-alive**
  `src/WebSocketTransportListener.cs` · `HandleWebSocketConnectionAsync`
  `while (State == Open) await Task.Delay(100);` pins an ASP.NET request per connection and
  adds up to 100 ms to close detection. It also never exits if the peer parks in
  `CloseReceived`.
  Fix: signal a `TaskCompletionSource` when the channel closes and await that instead.

- [ ] **2.7 — `Close()` blocks on `.Wait(1000)` inside a lock**
  `src/WebSocketMessageSocket.cs` · `Close`
  Sync-over-async while holding `m_socketLock`; risks stalls and thread-pool starvation.
  Fix: fire-and-forget the close handshake (or expose an async close) and dispose the socket.

- [ ] **2.8 — `Thread.Sleep(1000)` in the `ChannelFull` back-pressure loop**
  `src/WebSocketServerChannel.cs` · `ProcessRequestMessage`
  Inherited from the TCP port, but on the WebSocket path this blocks a thread-pool thread.
  Fix: revisit once 2.6 lands; prefer async delay.

---

## Phase 3 — Server hardening / security

- [x] **3.1 — No silent WSS → plaintext downgrade**
  `src/WebSocketTransportListener.cs` · `Start`
  Fixed: TLS is now required whenever `EndpointUrl.Scheme == Utils.UriSchemeOpcWss`; `Start()`
  throws `ServiceResultException(BadConfigurationError)` if no server certificate can be
  resolved, instead of silently binding plain HTTP. Also wired TLS client-certificate
  validation into Kestrel via the existing `m_quotas.CertificateValidator`.

- [ ] **3.2 — Do not hardcode `Basic256Sha256` for the TLS certificate**
  `src/WebSocketTransportListener.cs` · `Start`
  Derive the certificate from the configured endpoints / security policies; today an
  ECC-only or RsaPss-only server gets the wrong certificate or none.

- [ ] **3.3 — Verify TLS-layer behaviour explicitly**
  Add a check that `wss://` actually negotiates TLS with the expected certificate, and decide
  the policy for plain `ws://` (the SDK only defines `opc.wss`, no `opc.ws` constant, so
  WSS-only is defensible — but then non-wss input should be rejected explicitly).

- [ ] **3.4 — Replace the `CreateReverseConnection` dead code**
  `src/WebSocketTransportListener.cs`
  It assigns the events to `null` and invokes them "to suppress warnings" before throwing.
  Fix: delete the dead statements; keep `throw new NotImplementedException(...)` until
  reverse connect is actually implemented.

---

## Phase 4 — Deferred (client side, explicitly out of scope for now)

Recorded so they are not lost; the reference does not implement these either.

- [x] **4.1 — Implement `ConnectAsync`; retire `BeginConnect`**
  `IMessageSocket` declares only `ConnectAsync`, and `UaSCUaBinaryClientChannel` calls it
  for forward connects. Implemented, matching the reference `TcpMessageSocket.ConnectAsync`
  contract (throws on failure, no swallow-into-eventargs). `BeginConnect` was **not** retired:
  `WebSocketServerChannel.BeginReverseConnect` still calls it for the reverse-connect path,
  so both now share a private `ConnectClientWebSocketAsync` helper with the same fixes.
- [x] **4.2 — Client must offer the `opcua+uacp` subprotocol**
  Fixed in `ConnectClientWebSocketAsync`: `clientWebSocket.Options.AddSubProtocol("opcua+uacp")`.
- [x] **4.3 — Remove the client TLS validation bypass**
  Removed the `RemoteCertificateValidationCallback = (…) => true` bypass entirely; the
  client now relies on the default .NET/OS TLS trust-store validation. Full delegation to
  the OPC UA `ICertificateValidator` isn't wireable here: `IMessageSocketFactory.Create(sink,
  bufferManager, receiveBufferSize)` and `IMessageSink` don't expose it, so the socket layer
  has no access to `Quotas.CertificateValidator`.
- [ ] **4.4 — Implement reconnect paths**
  `WebSocketListenerChannel.Reconnect`, `ReconnectToExistingChannel` and
  `TransferListenerChannelAsync` all throw. `WebSocketServerChannel` already contains the
  full queued-response reconnect logic (faithful port of `TcpServerChannel`) — it is just
  unreachable. Depends on 2.1.
- [ ] **4.5 — Enforce message-size quotas on receive**
  The SDK validates `TcpMessageType.IsValid` and rejects
  `messageSize > receiveBufferSize` before reading the body. We never inspect the header,
  so a peer can declare an oversized message. Phase 1.2 bounds reassembly in practice;
  add explicit validation for defence in depth.
