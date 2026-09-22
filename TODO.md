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

- [x] **2.1 — Connection loss is never reported to the channel**
  `src/WebSocketMessageSocket.cs`
  Fixed: `ReadNextMessageAsync` no longer swallows exceptions in its `catch` — it returns
  the rented buffer and rethrows, so the outer loop's `catch` in `ReadNextMessage` (which
  calls `OnReceiveError`) actually runs. `ReadNextMessageAsync` now returns `bool` (`false`
  when a close frame was received and already reported) so the loop can distinguish that
  case from falling through. After the loop exits for any other reason (e.g. the socket
  goes straight to `Aborted` on an abrupt reset, with no exception and no close frame), and
  it wasn't an intentional local `Close()`, `ReadNextMessage` now reports a generic
  `BadConnectionClosed` to the sink instead of exiting silently.

- [x] **2.2 — `MaxChannelCount` is computed but never enforced**
  `src/WebSocketTransportListener.cs` · `HandleWebSocketConnectionAsync`
  Fixed: when `!serveChannel` (still at/above `MaxChannelCount` after the idle-cleanup
  eviction attempt), the incoming `webSocket.Abort()` is now called and no channel/entry
  is created, instead of just logging and creating the channel anyway. Also dropped the
  hardcoded `bool isBlocked = false` (it never toggled and gated nothing).

- [x] **2.3 — Chunk ordering can be violated on send**
  `src/WebSocketMessageSocket.cs` · `Send`
  Fixed: `Send` no longer spawns a `Task.Run` per call. It enqueues the
  `WebSocketMessageSocketAsyncEventArgs` onto an unbounded `Channel` (FIFO), and a single
  background consumer (`ProcessSendQueueAsync`, started once per instance from both
  constructors) drains it and calls `WebSocket.SendAsync` one item at a time, in the order
  `Send` was called. `Close()` completes the channel writer so queued items still get
  `OnCompleted()` (marked as socket errors) and the consumer loop exits.

- [x] **2.4 — `UpdateChannelLastActiveTime` is a no-op + no inactivity timer**
  `src/WebSocketTransportListener.cs`
  Fixed: `UpdateChannelLastActiveTime` now parses the channel id out of `globalChannelId`
  (`{ListenerId}-{ChannelId}`, same convention as the SDK) and calls `UpdateLastActiveTime()`
  on the resolved channel. Added a `System.Threading.Timer` (`m_inactivityDetectPeriod =
  ChannelLifetime / 2`, started in `Start()`, disposed in `Dispose(bool)`) that runs
  `DetectInactiveChannels`, mirroring the SDK's `TcpTransportListener`: it scans `m_channels`
  for `ElapsedSinceLastActiveTime > Quotas.ChannelLifetime` and calls `IdleCleanup()` on each
  stale channel.

- [x] **2.5 — `OnRequestReceivedAsync` is `async void`**
  `src/WebSocketTransportListener.cs`
  The whole body was already wrapped in `try/catch (Exception)`, so nothing could escape
  the `async void` method and crash the process — but on failure it only logged, leaving
  the client hanging with no response for that request. Fixed to match the reference
  `TcpTransportListener`: the catch now also builds a `ServiceFault` via
  `EndpointBase.CreateFault` and sends it back on the channel (itself guarded by a nested
  try/catch, since the channel may already be gone).

- [x] **2.6 — Replace the per-connection 100 ms polling keep-alive**
  `src/WebSocketTransportListener.cs` · `HandleWebSocketConnectionAsync`
  Fixed: `WebSocketListenerChannel` now exposes a `Closed` task backed by a
  `TaskCompletionSource`, completed from `ChannelClosed()`, `ChannelFaulted()` and
  `Dispose(bool)` — the three terminal transitions (graceful close, fault, and direct
  disposal e.g. on listener shutdown). `HandleWebSocketConnectionAsync` captures
  `channel.Closed` right after `Attach` and awaits that instead of
  `while (State == Open) await Task.Delay(100);`, so it reacts immediately to the channel
  closing (including the `CloseReceived` case) instead of polling.

- [x] **2.7 — `Close()` blocks on `.Wait(1000)` inside a lock**
  `src/WebSocketMessageSocket.cs` · `Close`
  Fixed: `Close()` now only takes `m_socketLock` to flip `m_closed` and grab/null the
  `WebSocket` reference, then releases it before doing anything else — no I/O happens while
  the lock is held. The close handshake (`CloseAsync` with a 1s timeout) and the socket's
  `Dispose()` are moved to a fire-and-forget `CloseWebSocketAsync` task, so `Close()` (and
  any caller holding a channel-level lock around it) never blocks on network I/O.

- [x] **2.8 — `Thread.Sleep(1000)` in the `ChannelFull` back-pressure loop**
  `src/WebSocketServerChannel.cs` · `ProcessRequestMessage`
  Confirmed worse than described: `HandleIncomingMessage` calls `ProcessRequestMessage` while
  holding `lock (DataLock)`, so the blocking `Thread.Sleep(1000)` loop (up to 5s) both tied up
  a thread-pool thread (this runs on the `Task.Run` read loop from `WebSocketMessageSocket`,
  unlike the original TCP transport's IOCP callback thread) and froze every other operation on
  the channel for its duration. Fixed: split `ProcessRequestMessage` into the (still
  lock-protected) security validation, and `ProcessRequestMessageWhenNotFull` /
  `ProcessValidatedRequestMessage`. When `ChannelFull`, instead of sleeping it schedules a
  `Task.Delay(1000)` continuation that re-checks and re-acquires `DataLock` only when it runs,
  and returns `true` (ownership of the message buffer retained for the deferred retry)
  immediately — no thread is blocked and the channel lock is not held across the delay.
  Gives up (same as before, `ChannelClosed()`) after 5 retries.

---

## Phase 3 — Server hardening / security

- [x] **3.1 — No silent WSS → plaintext downgrade**
  `src/WebSocketTransportListener.cs` · `Start`
  Fixed: TLS is now required whenever `EndpointUrl.Scheme == Utils.UriSchemeOpcWss`; `Start()`
  throws `ServiceResultException(BadConfigurationError)` if no server certificate can be
  resolved, instead of silently binding plain HTTP. Also wired TLS client-certificate
  validation into Kestrel via the existing `m_quotas.CertificateValidator`.

- [x] **3.2 — Do not hardcode `Basic256Sha256` for the TLS certificate**
  `src/WebSocketTransportListener.cs` · `Start`
  Fixed: added `GetTlsServerCertificate()`, which reads the distinct `SecurityPolicyUri`s
  actually configured on this listener's `m_descriptions` (populated per-endpoint by
  `WebSocketServiceHost.CreateServiceHost` from the app's configured security policies, not
  hardcoded), tries each with `GetInstanceCertificate` (preferring one that requires a
  certificate over `SecurityPolicies.None`, tried last as a fallback), and returns the first
  certificate found. An ECC-only or RsaPss-only server (or one that never configured
  `Basic256Sha256` at all) now gets its actual certificate for the TLS handshake instead of
  unconditionally failing to resolve one.

- [x] **3.3 — Verify TLS-layer behaviour explicitly**
  `src/WebSocketTransportListener.cs` · `Start`
  Fixed the policy half: the SDK only defines `opc.wss`, no `opc.ws` constant, so this
  transport is WSS-only. `Start()` now checks `EndpointUrl.Scheme` up front and throws
  `ServiceResultException(BadConfigurationError)` for anything other than
  `Utils.UriSchemeOpcWss`, instead of silently computing `requireTls = false` and binding an
  unencrypted listener for any other scheme. TLS setup (certificate lookup, `UseHttps`) is now
  unconditional since only `wss://` can reach it. (Actually exercising the negotiated TLS
  handshake against the expected certificate is left for the opcjs smoke test in 1.7 rather
  than new unit-test infrastructure here.)

- [x] **3.4 — Replace the `CreateReverseConnection` dead code**
  `src/WebSocketTransportListener.cs`
  Fixed: removed the `= null` assignments and the `?.Invoke(null, null)` calls that existed
  only "to suppress warnings"; `CreateReverseConnection` now just logs and throws
  `NotImplementedException`. Removing that dead code left `ConnectionStatusChanged` with no
  raise site, triggering CS0067 (it's mandated by `ITransportListener`, not something we can
  delete) — suppressed with a comment noting it stays silent until reverse connect (4.4) is
  implemented.

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
