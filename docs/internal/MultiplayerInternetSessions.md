# Multiplayer Internet sessions — design record

Status: Implementing (WS7 of the live-multiplayer plan, part 2: Internet sessions in the game) · Date: 2026-10-17 ·
Owner: multiplayer

This records how the game hosts and joins over the Internet through the TopiaForge service: the provider's client for
the control plane, the hosting and joining flows built on it, the failure codes they report, and what the in-game
interface shows for each. The service's API is recorded in `ControlPlaneApi.md`, the NAT layer the flows drive in
`MultiplayerNatTraversal.md`, the LAN and direct flows they extend in `MultiplayerLiveSessions.md`, and the internal SPI
in `MultiplayerRuntimeIntegration.md`. The player-facing wording on addresses is `MultiplayerPrivacyText.md`, used word
for word.

The code lives in `mods/TopiaForge.Multiplayer/ControlPlane/`, inside the provider only, and every type is internal.
The live flows in `Network/` and the internal control in `Provider/` call it.

## 1. Shape

| Part | Runs on | Does |
| --- | --- | --- |
| `ControlPlaneOrigin` | Any thread | Reads the service's origin, in its one canonical spelling |
| `HttpsControlPlaneTransport`, `HttpAnswerReader` | Thread-pool tasks | One bounded HTTPS exchange: no proxy, no redirect, one deadline, a strict HTTP/1.1 reader |
| `SignalingUpgrade` | Thread-pool tasks | Opens the signaling socket over the HTTPS client's own connection: the upgrade, read as strictly, then `WebSocket.CreateFromStream` |
| `ControlPlaneClient`, `ControlPlaneAnswers` | Thread-pool tasks | Every endpoint the game uses, with canonical request bodies and strict readers for the answers |
| `ServiceResult`, `ServiceErrors` | Any thread | A request's outcome; the error catalogue, read by code and status alone |
| `InternetServices` | Any thread | What the flows reach the service with: the client, the signaling socket's opener and the name resolver |
| `InternetHost` | The main thread | A hosted session's life with the service: registration, heartbeat, invites, descriptor, relay and NAT hosting |
| `ServiceAwareResolver` | Thread-pool tasks | Resolves invites and listed servers through the service; hands every other target on |
| `InternetJoinRoute` | The main thread | A join through the service: ticket, relay credentials, the join's network, and the NAT join |
| `InternetFailures` | Any thread | Maps service and NAT outcomes to the live sessions' failures, and words each in this game's own sentences |
| `InternalControl.Internet` | The main thread | The SPI's Internet members and the status it reports |

Every request runs off the main thread and lands at a later pump, so nothing here blocks a frame. A host's own steps
run one at a time, and so do a join's; the relay credential renewals the NAT layer asks for run beside them.

## 2. Where the service comes from

The origin is read once, when the runtime attaches to the internal control:

1. **The session launch.** A launch request V5 (`MultiplayerLaunchContract.md`) carries `serviceOrigin`.
2. **The manager's configuration.** The launcher's multiplayer preferences projection
   (`multiplayer-preferences-v1.json` in the manager's staging folder) carries the configured origin.
3. **Neither.** No service is configured. Nothing is ever contacted, and the session offers only LAN and direct play.

The ModManager plugin reads both at startup and sets `IInternalMultiplayerRuntimeHost.ServiceOrigin` to the first that
names one (`MultiplayerRuntimeHost.ConfigureServiceOrigin`). The launch's session goes to the provider apart from its
origin, as a call on the internal control once mods have loaded, so a direct target's address does not stay in memory.

- **Spelling.** The game accepts only the spelling both sources write: `https://`, a lowercase DNS name, and an
  optional port of 1 to 65535 other than 443, with no trailing slash, path, query or credentials, and at most 256
  characters. An IP literal is refused, as the launch request refuses it. The launcher normalizes a hand-typed origin
  into exactly this spelling before it writes one.
- **Refusal.** An origin that does not read as one leaves Internet play off. The runtime gets a warning,
  `internet.origin-invalid`, which does not repeat the text.
- **Development.** Tests and a control plane run on this machine use `ControlPlaneOrigin.Loopback`, plain http to
  `127.0.0.1` or `::1`. No configuration can name it.

Without a service, `StartHosting` refuses Public and Invite visibility as `Unavailable` with the sentence in
`InternalControl.NoService`, and `Join` refuses an invite or a listed server the same way. `Query` answers
`ServiceNotConfigured`. LAN hosting, LAN joins and direct joins are unchanged.

## 3. The HTTPS client

The game uses its own small HTTP/1.1 client over `TcpClient` and `SslStream`, not `HttpClient`. Under the game's Mono,
`HttpClientHandler` delegates to `MonoWebRequestHandler`, whose proxy, redirect and buffering behaviour is the
runtime's. The small client keeps every rule in this code, and runs the same on Mono and on .NET.

- **One connection per request**, to the origin's own name and port, with `Connection: close`. No other host is ever
  contacted: a request carries a path under `/v1/`, never a URL.
- **No proxy.** The client never asks the system for one, and neither does the signaling socket, which opens over a
  connection the client makes.
- **No redirect.** An answer in the 300s is the failure `Redirected`.
- **Headers.** Exactly `Host`, `User-Agent: TopiaForge`, `Accept: application/json`, `Authorization: Bearer` when the
  request has a credential, and `Content-Type: application/json` with `Content-Length` when it has a body. No cookie,
  no compression.
- **Names.** The system resolver looks the name up within the request's deadline. At most four IPv4 and then four IPv6
  addresses are tried in turn; unspecified, broadcast and multicast addresses are skipped.
- **TLS.** The certificate must verify for the origin's name with no error at all. Under the game's Mono that is
  UnityTLS, which offers TLS 1.2.
- **Deadlines.** Each connection attempt has 10 s, and the whole request, from the lookup to the last byte of the
  answer, has 20 s. A service that answers a byte at a time cannot hold a request past it.
- **The answer.** The status line and at most 64 header lines in 16 KiB, each ending in CR LF; folded or malformed
  lines are refused. The body is framed by `Content-Length`, chunked encoding or the end of the connection, and an
  answer that names both framings is refused as ambiguous. A chunked body's framing, its size lines, their extensions,
  its line ends and its trailers, shares one budget of 16 KiB plus the body's bound, so tiny chunks behind long
  extensions cannot make an answer cost the wire more than twice its bound and a header block. At most four interim
  answers are skipped; a protocol switch is refused. `Retry-After` is kept only as plain whole seconds up to 86,400.
- **Bounds.** 4 KiB for most answers, 16 KiB for relay credentials, and 576 KiB for a server's answer, which carries a
  whole signed descriptor as an escaped string.
- **Failures.** Nothing throws out of the transport. A failure is `Unreachable`, `TimedOut`, `Tls`, `Redirected`,
  `TooLarge`, `Malformed` or `Cancelled`, and no exception's text is read.
- **The signaling socket.** `SignalingUpgrade` opens it over a connection made exactly as a request's is: the same
  lookup, attempts and certificate check, and the whole open within the 20 s as well as the signaling client's own
  10 s. It writes `GET /v1/signal/{serverId}` with exactly `Host`, `User-Agent: TopiaForge`, `Upgrade: websocket`,
  `Connection: Upgrade`, a fresh 16-byte `Sec-WebSocket-Key`, `Sec-WebSocket-Version: 13` and the credential's
  `Authorization: Bearer`, then reads the answer's head by the rules above, skipping no interim answer. Only an
  HTTP/1.1 `101` with one `Upgrade: websocket`, a `Connection` that names the upgrade and one `Sec-WebSocket-Accept`
  made from the key opens it. Header names and the `websocket` and `upgrade` tokens are read in any case, and
  `Connection` may list other tokens, as RFC 7230 allows. Any other status, a redirect included, is a refusal and is
  never followed, and so is an extension or subprotocol never offered, or a body's framing. The connection, with any
  bytes that came behind the head, then goes to `WebSocket.CreateFromStream`, so no runtime's own WebSocket client,
  with its own proxy and redirect rules, is involved. Opening ends as unreachable when the connection fails, times
  out or ends before the head does, and as refused for every answer that does not upgrade; either way the connection
  is closed at once.

## 4. Reading the service's answers

Every answer is hostile input until it reads.

- **Canonical JSON.** A successful answer must be canonical, as the service writes every one, within its bound. Members
  the client does not know are ignored, as the API allows. A reader returns nothing for anything else, which is the
  failure `InvalidResponse`.
- **Every member it uses** is present and valid: types, ranges, the exact shapes of ids, codes and links. The status
  must have schema `topiaforge.control-plane-status/1` and `api` 1, a heartbeat of 5 to 300 s, a listing lifetime
  longer than the heartbeat and at most an hour, and a credential lifetime of 60 s to a day.
- **Agreement with the request.** A registration names this server and this descriptor's SHA-256. A server's answer
  carries a descriptor that verifies for the server id, whose exact bytes hash to the answer's `descriptorSha256`, and
  whose listing agrees on both. A join ticket names this server and carries the joiner's own privacy choice: one that
  says otherwise is not this join's.
- **Errors** are read by code and status alone. A code counts only with the status the catalogue gives it; anything
  else is `RateLimited` for a 429, whatever page the platform in front of the service wrote, `Refused` for any other
  4xx and `Failed` for a 5xx. The error's message is never read, and nothing the service wrote is kept, logged or shown.
- **Credentials.** Tokens, tickets, challenges and invite codes are held to their alphabets and lengths, and no type
  that holds one prints it.

## 5. Hosting

A Public or Invite session with a service configured gives its host an `InternetHost`. The NAT layer shares the host's
game socket, so joiners through the service reach the same session as everyone else. LAN visibility never creates one,
so a LAN host never contacts the service.

| Step | Request | On success | On failure |
| --- | --- | --- | --- |
| Status | `GET /v1/status` | Registration may start | Retried with backoff |
| Challenge | `POST /v1/hosts/challenge` | The proof can be signed | Retried with backoff |
| Register | `POST /v1/hosts` | Online: a session token, the first invite, and whether the server is listed | Retried from a fresh challenge |
| Heartbeat | `POST /v1/hosts/{id}/heartbeat` every heartbeat, with the player count | The listing is renewed | Retried within the listing's lifetime |
| Descriptor | `PUT /v1/hosts/{id}/descriptor` when the world changes | The service lists the newer descriptor | Tried again after the next heartbeat, and never sooner than `Retry-After` |
| Relay | `POST /v1/turn/credentials` with the session token | NAT hosting starts | See relay-only below |
| Stop | `DELETE /v1/hosts/{id}` | The listing, invites, tickets and signaling room end | The listing expires by itself |

- **Degradation.** Hosting or signaling turned off, or invites turned off for an invite-only session, means nobody could
  join: the host reports `ServiceDisabled` and reads the status again a minute later. Relay turned off, or the kill
  switch engaged, means the relay is unavailable (below). The service's feature-disabled refusals are handled the same.
- **The proof.** The Ed25519 signature by the host's identity key covers `TopiaForge host registration v1\n` followed
  by the canonical `{"challenge","descriptor","relayOnly"}`. The signing seed is copied for the one signature and wiped
  at once.
- **Online.** In order of priority: the heartbeat when it is due, the player's invite requests, a newer descriptor, and
  then the relay. A heartbeat that fails is retried with backoff, no later than the next heartbeat would be, unless the
  service named a longer delay.
- **Lapse.** A heartbeat refused as `unauthorized`, or no heartbeat landing for the listing's whole lifetime, means the
  service ended the session. The host registers again, at most once a minute, with fresh signaling and a fresh invite.
  NAT hosting restarts with it, so players connected through the old registration lose their connection and join
  again with the new invite.
- **Descriptor.** `LiveHost.UpdateWorld` signs a newer descriptor, and the next free moment publishes it. A
  `stale-descriptor` answer means the service already holds a newer one, which stands. One the service turns away for
  now, failing or rate-limited, is sent again after the next heartbeat and never sooner than a delay the service named,
  so at most once a heartbeat. A descriptor the service refuses outright is not sent again.
- **Invites.** Registration brings the session's first invite, valid for seven days. `CreateInvite` asks for another,
  also for seven days, which becomes the one the status shows once it lands; earlier ones keep working until they
  expire or are withdrawn. `RevokeInvite` withdraws one, and a `not-found` answer counts as done. A session holds at
  most eight live invites. Invites are not renewed by themselves.
- **NAT hosting.** Relay credentials minted with the session token start `NatHost` on the game socket. The session
  plan opens the signaling room with the current session token and renews the credential with it. Host candidates
  exist only for a host that allows direct connections, and the router is asked to forward the port only when the
  host also turned on `RouterForwarding`.
- **Stopping.** `StopHosting` sends the delete. A registration still in flight is deleted as soon as it lands, so no
  listing outlives the host. Stopping cancels the host's other requests but never a registration: the service may
  already hold one whose answer is still on its way, and only that answer's token can delete it, so the registration
  keeps its own 20 s deadline instead. The NAT layer is left to the network: its links close first, so the message
  ending each player's session still leaves through the relay, and then the network disposes the NAT layer, which says
  goodbye and releases the relay. When the provider is disposed as the game closes, it waits up to 2 s for the delete
  with the last saves. A game that is killed leaves its listing to expire after the listing's lifetime, 90 s on staging.

### Backoff

| Case | Wait |
| --- | --- |
| The first retry of a failed step | 5 s, doubling to at most 5 minutes |
| `rate-limited`, or any other 429 | At least `Retry-After`, and at least 5 s |
| A newer descriptor the service turns away for now | Until the next heartbeat, and at least `Retry-After` |
| Any other refusal that names `Retry-After`, a 503 included | The longer of `Retry-After` and the host's own wait |
| Hosting, signaling or invites turned off | 60 s, then the status is read again |
| `server-blocked` | 15 minutes |
| Registering again after a lapse | At least 60 s after the last registration: the service allows six a minute |
| A refused relay mint for a relay-only host | 30 s, doubling to at most 5 minutes, and at least `Retry-After` |
| A refusal nothing here can change: an invalid descriptor, a proof that does not verify, a refused request | Never: the listing fails until hosting starts again |

## 6. Joining

`TryCreateJoinTarget` reads an invite code (`ABCDE-12345`, any case, or `topiaforge://join/<code>`) or a server link
(`topiaforge://join/server/<server id>`). `Join` then runs the same `LiveJoin` as a LAN or direct join, with the
service in its first, fourth and fifth steps:

1. **Resolve.** `GET /v1/status` first, so a join learns at once that Internet joining or invites are off, then
   `POST /v1/invites/resolve` with the canonical code, or `GET /v1/servers/{id}`. The answer's descriptor has been
   verified for its server id before the join sees it. An invite a session launch pinned must still lead to the server
   the launcher verified.
2. **Check.** The build, the protocol and the content, as for any join. While the status says the relay is
   unavailable, a relay-only join, or any join to a host that hides its address, fails here as `RelayUnavailable`,
   before a ticket is asked for or a socket opens.
3. **Password**, when the session needs one, as for any join.
4. **Ticket and relay.** `POST /v1/join-tickets` carries the player's privacy choice, `relayOnly`, and the invite for an
   invite-only server. Relay credentials are minted with the ticket.
5. **Path.** The join's network opens with its transport and its NAT agent both relay-only unless the player allowed a
   direct connection for this join. `NatJoin` signals the host and finds the path, and the connection attempt it fills
   in goes to the session engine exactly as a direct one does.

- **Relay-only, the default, is honoured whatever it costs.** A refused mint (`relay-unavailable`, `ticket-limit` or
  `upstream-failed`) ends a relay-only join as `RelayUnavailable` before any socket opens. Nothing falls back to a
  direct path, and the agent and transport are both relay-only, so neither could take one.
- **Direct is only an explicit choice for one join.** `InternalJoinSettings.AllowDirect` is set by the interface after
  the player chose "Allow for this session" in the approved prompt (section 7). Only then does the ticket say direct,
  the joiner list its own candidates, and a join to a host that allows direct connections go on without the relay,
  on direct paths alone, when the relay is unavailable.
- **Reconnecting.** A link lost in session is retried within the engine's grace period with a fresh NAT join on the
  same network and the same ticket, minting again only when the credential has less than two minutes left.
- **Leaving.** The join's network stops first, so its last frames still leave through the relay, and then the NAT layer
  says goodbye and releases the relay.
- **Making way.** When a host's session table is full, its NAT layer ends the oldest session with no connection whose
  checks ran out, without a goodbye. That joiner's checks had already run out, so its join ends as an ordinary
  `NoRoute`, and the host's flow sees nothing at all. No flow treats it as anything else.

`Query` resolves an invite or a listed server through the service the same way, and checks it as a join would, without
asking for a ticket.

## 7. Failure codes and what the interface shows

Every new failure has its own `InternalLiveFailure` code. A join's `JoinDetail` is the sentence in this table, from
`InternetFailures.Detail`, and never repeats anything the service or a host sent. A host's listing reports its code in
`Internet.ListingFailure`, its invite requests in `Internet.InviteFailure`, and the service's named delay in
`Internet.RetryAfterSeconds`; the interface words those itself.

| Code | Value | When | The join's sentence | The interface |
| --- | --- | --- | --- | --- |
| `ServiceUnavailable` | 21 | The service could not be reached, failed, or answered in a way this game cannot read | The TopiaForge service could not be reached. Try again shortly. | Offers to try again |
| `ServiceNotConfigured` | 23 | No service is configured | This game is not set up to reach the TopiaForge service, so invites and listed servers are unavailable. LAN and direct play still work. | HOST greys out Public and Invite with the `NoService` sentence; CONNECT accepts addresses only |
| `ServiceDisabled` | 24 | The operator turned hosting, invites or Internet joining off | The TopiaForge service has turned this off for now. Try again later. | Says so; the host keeps checking by itself |
| `ServiceRateLimited` | 25 | The service turns this network's requests away for a while | The TopiaForge service is turning away requests from this network for a moment. Try again shortly. | Counts down `RetryAfterSeconds` when it is not zero |
| `InviteNotFound` | 26 | Mistyped, expired or withdrawn, or the session ended | That invite code does not work: it may be mistyped, expired or withdrawn, or its session may have ended. | Keeps the code for correction |
| `ServerNotListed` | 27 | The server is not listed any more, or never was | That server is not listed any more. | Drops it from recent servers |
| `RelayUnavailable` | 28 | The relay is unavailable and the session keeps to it | See below | See below |
| `UdpBlocked` | 29 | No STUN or TURN server answered | This network blocks the connections Internet play needs. | Suggests another network |
| `HostOffline` | 30 | The host is not connected to signaling | The host is not online right now. | Offers to try again |
| `HostNotAnswering` | 31 | The host never answered the offer | The host did not answer. | Offers to try again |
| `NoRoute` | 32 | No path worked, through the relay or directly | No connection to the host could be made. | Offers to try again |
| `SignalingFailed` | 33 | The signaling socket could not be opened or kept | The connection to the TopiaForge service was lost before the host could be reached. | Offers to try again |
| `InviteWithdrawn` | 34 | The host withdrew the invite, or the server stopped being public | The host withdrew the invite, or the server stopped being public. | Asks for a new invite |
| `ServerBlocked` | 35 | The operator holds the server back | The TopiaForge service is holding this server back for now. | Says so; the host retries much later by itself |
| `ServiceRefused` | 36 | The service refused what this game sent | The TopiaForge service refused a request from this game; it may need an update. | Suggests updating |
| `InviteLimit` | 37 | The session holds eight live invites | This session already has as many invites as it may hold. Withdraw one, or wait for one to expire. | SESSION offers to withdraw the current invite |

**Relay unavailable.** The sentence depends on the host:

- When `Internet.HostAllowsDirect` is true: "The relay is unavailable right now, and Hide my IP keeps this session on
  the relay, so it did not connect." The interface may then show the approved prompt, titled **Allow a direct
  connection?**, with its body from `MultiplayerPrivacyText.md` word for word and the buttons **Allow for this
  session · Cancel**. Allow starts a new `Join` with `AllowDirect` for that join only. Cancel leaves Hide my IP on, and
  the join does not happen.
- When it is false: "The host keeps its address hidden behind the relay, and the relay is unavailable right now." No
  prompt is shown, since a direct connection would not help.
- A relay-only host the service gives no relay is registered but unreachable over the Internet: its listing reports
  `RelayUnavailable` while it keeps asking, and it never offers a direct path instead. HOST shows that players cannot
  reach the session until the relay returns.

## 8. The SPI

| Member | Use |
| --- | --- |
| `IInternalMultiplayerControl.CreateInvite()` | Host only: asks for another invite. Refused as `InvalidState` until the session is registered |
| `IInternalMultiplayerControl.RevokeInvite(code)` | Host only: withdraws an invite, typed as people read it or canonical |
| `IInternalMultiplayerRuntimeHost.ServiceOrigin` | The manager's configured origin, or null |
| `InternalHostSettings.RouterForwarding` | A direct-allowing host's consent to ask the router to forward the port |
| `InternalLiveStatus.Internet` | An `InternalInternetStatus` (below) |
| `InternalLiveFailure` 23 to 37 | Section 7; `ServiceUnavailable` (21) now means the service failed, not that this build lacks it |

`InternalInternetStatus` holds, as a host, `ServiceConfigured`, `ListingPhase` (`Off`, `Registering`, `Online`,
`Retrying`, `Failed`), `ListingFailure`, the current `Invite` (`InternalInvite`: code, display form, link and expiry),
`InviteFailure`, `Listed`, `RelayReady`, `RelayedPlayers`, `DirectPlayers` and `RetryAfterSeconds`; as a joiner,
`JoinRoute` (`InternalConnectionRoute`: `None`, `Relayed`, `Direct`), `RelayReady`, `HostAllowsDirect` and
`RetryAfterSeconds`. No member names an address. An `InternalInvite` prints only "invite", and a join target made from
an invite code prints "invite" without the code, since anyone holding a code may join.

## 9. Privacy

- **Addresses.** No peer, candidate or observed address reaches mod code, `MultiplayerSessionSnapshot`, logs,
  diagnostics or crash reports. The service's answers carry none, and the NAT layer's status is enums and counts.
- **This machine's addresses** are listed only for a session that allows direct connections, host or joiner. A
  relay-only session never lists them.
- **Credentials.** Session tokens, join tickets, challenges and invite codes are never logged or printed. Tokens and
  tickets travel only in the `Authorization` header, and invite codes only in request bodies, never in a URL.
- **The service's words.** No message the service wrote is read, and no exception's text is. Every sentence the
  player sees is this game's own.
- **Tests.** `InternetPrivacyTests` runs hosting, joining, failures and reconnection, then scans everything the flows
  report for the addresses in play. The session tests check every layer of relay-only on its own: what each machine
  sent, that the signaling service had nothing to strip, that the NAT agent and the transport are each relay-only, and
  that this machine's addresses were never listed. The architecture test lets only the provider's network folders,
  `ControlPlane` among them, use `System.Net`, and the SPI scan finds no member that names an address.

## 10. Tests

`tests/TopiaForge.Mods.Multiplayer.Tests`, in the multiplayer harness. Every test binds loopback, or uses the virtual
network.

| Class | Covers |
| --- | --- |
| `ControlPlaneClientTests` | Requests and answers byte for byte against the shared recordings in `tests/fixtures/multiplayer/control-plane/`; the wire form; errors read by code and status; strict readers |
| `ControlPlaneHttpTests` | Oversized, malformed, slow and silent answers; a TLS handshake that never finishes; immediate plaintext refused as TLS; redirects; certificate names; no proxy; a fuzz of hostile answers that must never throw |
| `SignalingUpgradeTests` | The signaling socket's upgrade: 301, 302, 307 and 308 never followed; answers of the wrong shape refused, and their connection closed at once; answers spelled any way the RFCs allow opened; the HTTPS client's deadlines, a TLS handshake that never finishes included, and its certificate check; exactly the headers it needs |
| `ControlPlaneFakeTests` | The fake service answers as the recordings do |
| `InternetConfigurationTests` | A LAN host contacts nothing; a launch's origin comes first; invite and listing launches go through the service; the signaling socket asks no proxy the system or the environment names |
| `InternetHostingTests` | Heartbeats, expiry and re-registration; 429 and 503 with backoff and `Retry-After`; the kill switch and degraded status; hostile answers; invites through the SPI; descriptor updates |
| `InternetSessionTests` | An invited joiner through symmetric NATs, relay-only; a direct upgrade only when the joiner chose it; a host that quits takes its listing; stopping hosting deletes the listing; reconnecting through the service |
| `InternetFailureTests` | Relay-only honoured when the relay is unavailable, at the status, the mint and the host; every join failure with its code and sentence; no service, no contact |
| `InternetPrivacyTests` | No address in anything the flows report; types print only their kind |

The fake control plane (`FakeControlPlane`) is built from the shared fixtures: answers in the shapes they record, the
error catalogue and its fixed messages byte for byte, challenges good once, proofs it checks, listings that lapse,
tickets that carry the joiner's privacy, and relay credentials minted on the NAT simulator's TURN server.
`ControlPlaneFakeTests` holds it to the recordings. Its knobs make it degraded or hostile: flags and the kill switch,
refusals with or without `Retry-After`, and answers that are oversized, malformed or slow. The end-to-end tests run it
with the NAT simulator, `FakeSignalingService` and `FakeTurnServer` on the virtual network.

## 11. Staging spike and in-game measurement (2026-10-17)

**The spike.** `dotnet run --project tests\TopiaForge.Mods.Multiplayer.Tests -c Release -- --staging` runs the
provider's own HTTPS client, signaling client and NAT layer against `https://mp-staging.topiaforge.dev` and its relay.
CI never passes the flag. It registers a fresh relay-only, invite-only test server, beats once, makes, resolves and
withdraws an invite, takes a relay-only ticket, mints credentials for both sides, joins through two real relay
allocations, passes a frame each way, and deletes the server. A heartbeat after the delete is refused as
`unauthorized`, which proves the token opens nothing. Its sockets bind nothing explicitly: each is connected to the one
relay it talks to. It prints statuses, shapes and timings only.

On .NET, from a Windows 11 x64 machine: every step passed in 12.8 s, 6 s of it a deliberate wait before the
heartbeat. The status took 211 ms, registration 1,141 ms, a ticket 255 ms, and the relay-only join was established
680 ms after the host's relay came up. Staging offers two UDP relay addresses and two STUN servers per credential, for
600 s.

**In the game.** The NAT layer's record noted that Mono's `ClientWebSocket` had been measured only against public echo
servers. This slice's design depends on it, and on `SslStream` over UnityTLS for the HTTPS client, so the same spike
ran inside Robotopia build 2545. A throwaway BepInEx plugin compiled the provider's own sources unchanged and ran from
an overlay root (`MultiplayerMonoSpike.md`, S1). The runtime was Mono 6.13.0 with the `unitytls` provider. Two runs:

| Step | Run 1 | Run 2 |
| --- | --- | --- |
| Status, first request (lookup, TCP, TLS) | 273 ms | 171 ms |
| Status, again (a new connection) | 974 ms | 160 ms |
| Challenge | 1,233 ms | 216 ms |
| Registration, with the Ed25519 proof | 847 ms | 742 ms |
| Heartbeat | 200 ms | 143 ms |
| Create, resolve and withdraw an invite | 575, 242 and 223 ms | 653, 283 and 206 ms |
| Relay-only join ticket | 429 ms | 296 ms |
| Mint for the joiner, then the host | 477 and 243 ms | 484 and 232 ms |
| `ClientWebSocket` to the Worker: open, then the first frame (welcome) | 1,324 ms, then 12 ms | 199 ms, then 10 ms |
| Host relay allocation | 48 ms | 49 ms |
| Relay-only join, established | 612 ms | 840 ms |
| A frame each way through the relays | 17 ms | 20 ms |
| Delete, then a heartbeat with the old token | ok, `unauthorized` | ok, `unauthorized` |
| Whole run | 15.6 s | 12.6 s |

- Both runs started 8 s after the plugin loaded. Run 1 was slower on most steps, up to 1.3 s for the challenge and the
  WebSocket's open, and failed none; the cause was not isolated.
- Neither side sent anything anywhere but its relay.
- Every server the spikes registered was deleted, and the overlay was removed.
- The launch overwrote the game's saved window resolution. The two values were restored afterwards. A rig must
  snapshot and restore the game's `Screenmanager` values, as `MultiplayerMonoSpike.md` warns, even without size
  arguments.

**Verdict.** The design holds under the game's Mono: the HTTPS client, the WebSocket signaling against the Worker, and
relay-only joins through Cloudflare's relay all work. A single request, or the signaling socket's open, can take over a
second in the game; every deadline in the client is at least 10 s.

**Since then.** The signaling socket no longer uses `ClientWebSocket`: it opens over the HTTPS client's own
connection (section 3). `WebSocket.CreateFromStream` over UnityTLS has not been measured in the game yet.

## 12. Limits and choices

- **Browsing** (`GET /v1/servers`) and the host view (`GET /v1/hosts/{id}`) are not used by the game. The launcher owns
  the Internet browser; a listed server reaches the game as a server link or a session launch.
- **Re-registration after a lapse** restarts NAT hosting under the new session token, so players who joined through
  the service join again with the new invite rather than resuming.
- **A host that allows direct connections and starts without the relay** stays on direct paths until it registers
  again; it does not add the relay later.
- **Invites** last seven days and are not renewed by themselves; a session that runs longer makes a new one.
- **Public and Invite hosting without a service** are refused rather than quietly hosting on the LAN, so the player
  never believes friends can reach a session they cannot.
- **The listing's world** follows `LiveHost.UpdateWorld`; the SPI has no member of its own for it yet.
- **The signaling socket's handshake is the game's own** (section 3), so both runtimes open it by the HTTPS client's
  rules. It was the runtime's `ClientWebSocket` before, whose handshake followed each runtime's rules: under the
  game's Mono 6.13, measured in the game on 2026-10-17, it followed no 302 or 307 the origin answered its upgrade with
  and used no proxy, whether one was set on its options or named by environment variables; on .NET, where the tests
  and the staging spike run, it asked no proxy but followed a redirect to another ws or wss address, without the
  `Authorization` header.
