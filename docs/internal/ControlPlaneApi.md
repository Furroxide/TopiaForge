# Multiplayer control plane API

Status: Implementing (WS7 of the live-multiplayer plan) · Date: 2026-10-14 · Owner: multiplayer

The control plane is the hosted half of Internet play: the server browser, invite codes, hole-punch signaling and relay
credentials. It is a Cloudflare Worker with SQLite-backed Durable Objects, in `services/control-plane/`. LAN discovery
and direct connections never touch it, and a client with no service configured contacts nothing.

This document is the contract the game's provider and the launcher implement against. The identity, the signed server
descriptor, canonical JSON and invite codes are defined in [MultiplayerLiveSessions.md](MultiplayerLiveSessions.md);
nothing here redefines them. How the service is deployed and operated is in
[ControlPlaneOperations.md](ControlPlaneOperations.md).

## 1. Conventions

- **Origin.** Clients take the origin from configuration (the launcher's `TOPIAFORGE_CONTROL_PLANE_ORIGIN`). Every
  path below is relative to it and starts with `/v1`. A breaking change gets a new version prefix.
- **Bodies are canonical JSON** (MultiplayerLiveSessions.md, section 2), sent as `Content-Type: application/json`.
  The service refuses a body that is not already canonical, that has a member it does not expect or lacks one it
  needs, or that exceeds the endpoint's size bound. Methods without a body (`GET`, `DELETE`, and the bodiless `POST`s
  marked below) must send none.
- **Query strings** are accepted only by `GET /v1/servers`. Any other request with a query is refused.
- **Responses** are canonical JSON with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. A `204` has
  no body. Clients must ignore response members they do not know; the service adds members only within `/v1`.
- **Credentials** travel only in `Authorization: Bearer …`, never in a URL:

  | Credential | Shape | Issued by | Used for |
  | --- | --- | --- | --- |
  | Host session token | `tfh1_` + 43 base64url characters | `POST /v1/hosts` | The host's own endpoints, signaling as host, relay as host |
  | Join ticket | `tfj1_` + 142 base64url characters | `POST /v1/join-tickets` | Signaling as a peer, relay as a peer |
  | Admin token | Set by the operator | — | `/v1/admin` |

  Clients treat tokens, tickets and challenges as opaque strings. The service keeps only a hash of a session token.
- **Timestamps** are Unix seconds. **Server ids** are 64 lowercase hexadecimal characters.
- **Browsers are not clients.** The service sends no CORS headers, and every body must be `application/json`, so a web
  page cannot drive it from a player's browser.

### Errors

Every error is `{"error":{"code":"<code>","message":"<fixed text>"}}`. The message is fixed per code and never
contains anything the client sent or anything about the service's internals.

| Status | Code | Meaning | What a client does |
| --- | --- | --- | --- |
| 400 | `invalid-request` | Malformed, non-canonical, wrong members or out of bounds | Fix the request; never retry it unchanged |
| 400 | `invalid-descriptor` | The signed descriptor fails verification | Re-sign it |
| 400 | `unsupported-visibility` | A `lan` descriptor was sent | LAN sessions do not register |
| 401 | `challenge-invalid` | The challenge is unknown, used, expired or for another key | Request a new challenge |
| 401 | `proof-invalid` | The registration proof does not verify | Sign the exact registration again |
| 401 | `unauthorized` | Missing or wrong credential, or a session that has ended | Hosts register again; peers ask for a new ticket |
| 403 | `server-blocked` | An operator holds this server out for now | Show it; retry much later |
| 403 | `ticket-limit` | This ticket may mint no more relay credentials | Ask for a new ticket after reconnecting |
| 404 | `not-found` | Unknown resource, and every invite miss | Treat the target as gone |
| 405 | `method-not-allowed` | Known path, wrong method (`Allow` lists the right ones) | Fix the client |
| 409 | `stale-descriptor` | A newer descriptor is registered | Keep the newer one |
| 409 | `invite-limit` | The host holds the most invites it may | Revoke one first |
| 409 | `room-full` | The signaling room is full | Retry later |
| 413 | `payload-too-large` | Body over the endpoint's bound | Fix the client |
| 415 | `unsupported-media-type` | Body not `application/json` | Fix the client |
| 429 | `rate-limited` | A limit refused the request; `Retry-After` says when to try | Wait at least `Retry-After` seconds |
| 500 | `internal` | The service failed | Retry with backoff |
| 502 | `upstream-failed` | The relay provider did not answer | Retry with backoff, or continue without relay |
| 503 | `feature-disabled` | An operator turned this feature off | Read `GET /v1/status`; degrade |
| 503 | `relay-unavailable` | Relay is off, the kill switch is engaged, or today's relay budget is spent | Continue without relay |
| 503 | `service-unavailable` | The deployment is missing configuration | Treat the service as down |

### Rate limits

Limits apply per address prefix: the caller's /24 for IPv4 and /56 for IPv6, as the service sees it. The service
never stores the prefix itself, only a keyed hash of the prefix and the hour, kept at most two hours. Windows slide:
a request counts against the current window plus the overlapping part of the previous one.

| Scope | Per minute | Per hour |
| --- | --- | --- |
| Host challenges | 10 | 60 |
| Host registrations | 6 | 30 |
| Descriptor updates, counted apart from registrations | 6 | 30 |
| Server browsing | 60 | 1,200 |
| Join tickets | 20 | 120 |
| Invite misses (see section 6) | 10 | 30 |
| Signaling upgrades | 30 | 240 |
| Relay credential mints | 5 | 20 |
| Admin requests | 30 | 300 |

A host session also limits itself: heartbeats at least five seconds apart, six descriptor updates a minute and thirty
new invites an hour.

## 2. Status

`GET /v1/status`, no credential. Clients read it before hosting or joining and degrade from it.

```json
{"api":1,"features":{"hosting":true,"invites":true,"relay":true,"signaling":true},"hosting":{"heartbeatSeconds":30,"listingTtlSeconds":90},"killSwitch":false,"relay":{"credentialTtlSeconds":600},"schema":"topiaforge.control-plane-status/1"}
```

- `features.relay` is false when relay is turned off, when the kill switch is engaged, or when the deployment's relay
  provider is not fully configured: its TURN key, its spend ceilings and its relay address ranges. A client then offers
  direct and LAN play only, and says why.
- `killSwitch` is true while relay minting is halted for spend safety.

## 3. Hosting

### 3.1 Challenge

`POST /v1/hosts/challenge`, body at most 1 KiB: `{"signingKey":"<base64url Ed25519 public key>"}`.

`200` `{"challenge":"<opaque>","expiresAt":…}`. A challenge is bound to the signing key, valid for two minutes, and
accepted once.

### 3.2 Register

`POST /v1/hosts`, body at most 513 KiB:

```json
{"challenge":"…","descriptor":"<signed descriptor text>","proof":"<base64url Ed25519 signature>","relayOnly":true}
```

- `descriptor` is the signed descriptor's exact canonical text (MultiplayerLiveSessions.md, section 3), carried as a
  JSON string. Its visibility must be `public` or `invite`; a `lan` descriptor is refused.
- `relayOnly` is the host's privacy choice for Internet paths. When true, the service never forwards the host's own
  candidates, only relay ones (section 8).
- **The proof** is an Ed25519 signature, by the descriptor's signing key, over the 32-byte ASCII label
  `TopiaForge host registration v1\n` followed by the canonical JSON of the body *without* `proof`:
  `{"challenge":…,"descriptor":…,"relayOnly":…}`. It proves the host holds the key now, binds the challenge, and
  covers every member it sends.

`201`:

```json
{"descriptorSha256":"…","expiresAt":…,"heartbeatSeconds":30,"invite":{"code":"RJ9JMV1V09","display":"RJ9JM-V1V09","expiresAt":…,"link":"topiaforge://join/RJ9JMV1V09"},"listed":true,"listingTtlSeconds":90,"serverId":"…","sessionToken":"tfh1_…"}
```

- `listed` says whether a public server made it into the directory; a full directory shard refuses new listings
  without refusing the session.
- Registering again with the same key replaces the session token and keeps the session: its live invites and the join
  tickets issued for it stay valid. Host signaling sockets opened with the old token close (4409) as soon as the new
  registration is saved, and none can open with it afterwards, so the new `relayOnly` holds from then on. A session
  whose heartbeats had stopped is ended first, so its invites and tickets do not carry over.
- An operator's hold answers `403 server-blocked`.

### 3.3 Heartbeat

`POST /v1/hosts/{serverId}/heartbeat`, host token, body `{"players":N}` with `0 ≤ N ≤ maxPlayers`.

`200` `{"expiresAt":…,"heartbeatSeconds":30,"listed":…}`. Send one every 30 seconds. Without one for 90 seconds the
session ends: its listing disappears at once, and an alarm then delists it, revokes its invites, closes its signaling
room and deletes all of its state. A heartbeat after that is `401`, and the host registers again.

### 3.4 Host view, descriptor update and ending

- `GET /v1/hosts/{serverId}`, host token: `{"descriptorSha256","expiresAt","invites":[invite…],"listed","players","relayOnly","serverId","visibility"}`.
- `PUT /v1/hosts/{serverId}/descriptor`, host token, body `{"descriptor":"<signed descriptor text>"}`: the same
  server's newer descriptor. Its `issuedAt` may not be older than the registered one (`409 stale-descriptor`).
  Changing visibility or the game build moves the listing. `200` `{"descriptorSha256","expiresAt","listed"}`.
- `DELETE /v1/hosts/{serverId}`, host token: ends the session now. `204`.

### 3.5 Invites

An invite object is `{"code","display","expiresAt","link"}`: the canonical code, its `XXXXX-XXXXX` form, its expiry
and its `topiaforge://join/<code>` link.

- Registration creates the session's first invite, valid for seven days or until the session ends.
- `POST /v1/hosts/{serverId}/invites`, host token, body `{"ttlSeconds":N}` with five minutes to seven days: `201`
  `{"invite":…}`. A session holds at most eight live invites.
- `POST /v1/hosts/{serverId}/invites/revoke`, host token, body `{"code":"<canonical code>"}`: `204`, or `404` when it
  is not one of this session's live invites. The code travels in the body, so it never appears in a URL. The service
  withdraws the code from its invite index before it forgets it: if the index cannot be reached, the request fails with
  `500`, the invite stays live everywhere, and the host tries again.

Every invite dies with its session. Withdrawing an invite, or its expiry, also ends the join tickets issued with it
(section 5.2).

## 4. Server browser

### 4.1 Listings

`GET /v1/servers?build=<gameBuild>&protocol=<major>[&after=<serverId>][&limit=<1-100>]`, no credential.

`200` `{"next":"<serverId>"|null,"servers":[listing…]}` in server id order. Pass `next` as `after` for the next page.
Only public, live servers appear. A listing (schema `topiaforge.server-listing/1`) is:

| Member | From |
| --- | --- |
| `schema`, `serverId`, `descriptorSha256`, `visibility` | The session |
| `name`, `gameBuild`, `runtimeVersion`, `protocolVersion`, `gamemode`, `world`, `maxPlayers`, `passwordRequired`, `voice`, `hostServesPackages`, `packageDigest` | The signed descriptor |
| `packageCount` | The descriptor's package list |
| `players`, `updatedAt` | The latest heartbeat |
| `relayOnly` | The host's privacy choice, so a joiner can fetch relay credentials first |

A listing has no member that can hold an address, a port or a candidate. The browser shows listings, but a client
must verify the descriptor (4.2) before trusting anything in one.

### 4.2 Server detail

`GET /v1/servers/{serverId}`, no credential: `200` `{"descriptor":"<signed descriptor text>","descriptorSha256":…,"listing":…}`
for a public server, otherwise `404`. A client verifies `descriptor` against `serverId` exactly as the shared fixtures
require before using its transport key.

## 5. Joining

### 5.1 Invite resolution

`POST /v1/invites/resolve`, no credential, body `{"code":"<text>"}`. The code is read forgivingly
(MultiplayerLiveSessions.md, section 4), so a client may pass what the player typed.

`200` `{"descriptor":…,"descriptorSha256":…,"listing":…,"serverId":…}`. A code resolves only while it is one of a live
session's own live invites. The call is a POST so that codes never appear in a URL.

### 5.2 Join tickets

`POST /v1/join-tickets`, no credential, body `{"invite":"<code>"|null,"relayOnly":true|false,"serverId":"…"}`.

- A public server needs no invite. An invite-only server needs one of its own live invites. If an invite is given, it
  must be one of that server's.
- `relayOnly` is the joiner's privacy choice, carried inside the ticket, so the service enforces it on every
  signaling frame the joiner sends.

`201` `{"expiresAt":…,"hostRelayOnly":…,"relayOnly":…,"serverId":…,"ticket":"tfj1_…"}`. A ticket lasts six hours.

A ticket also carries, under its MAC, the host session it was issued for and a keyed hash of the invite it was issued
with, and it holds only while that admission still would:

- **The same session.** A host that ends its session, or lets it lapse, and registers again starts a new session, in
  which earlier tickets open nothing. Registering again while the session is live keeps it (section 3.2).
- **A ticket issued with an invite** holds while that invite is live. Withdrawing the invite, or its expiry, ends the
  ticket, on a public server as on an invite-only one.
- **A ticket issued without an invite** holds while the server stays public.

The service checks this whenever a ticket opens a signaling socket or mints relay credentials, and answers `401` once it
no longer holds: the player asks for a new ticket. A mint is checked twice, before the service asks the relay provider
and again once the provider answers, so a mint that was already waiting when its ticket stopped holding, or when it
expired, gets no credential either (section 7). The service also closes the ticket's open signaling socket with 4403.
A relayed player whose ticket stops holding cannot renew its relay credentials, so its relayed path ends when its
current credential expires.

## 6. One answer for every miss

Invite resolution and join tickets give the identical `404` for an unknown, malformed, revoked or expired code, for a
code of another server, and for a server that has gone: same status, headers and body. Every miss counts against the
caller's prefix, exactly, however many requests run at once: each invite resolution and join ticket request takes one
of the prefix's miss slots before it looks anything up, in the same step as the check, and gives it back unless it
missed. Once a prefix has missed ten times in a minute or thirty in an hour, its invite resolutions and join tickets
are refused with `429` before any lookup, so a refusal reveals nothing either. Codes carry 50 random bits.

## 7. Relay credentials

`POST /v1/turn/credentials`, host token or join ticket, body `{"serverId":"…"}`.

`201`:

```json
{"expiresAt":…,"iceServers":[{"urls":["stun:stun.cloudflare.com:3478"]},{"credential":"…","urls":["turn:turn.cloudflare.com:3478?transport=udp"],"username":"…"}],"ttlSeconds":600}
```

- The `iceServers` shape is Cloudflare Realtime TURN's. The service keeps only URLs that name a host by DNS name, so a
  response never holds an IP address. Exactly one entry carries `username` and `credential`.
- Credentials are short-lived (`ttlSeconds`, ten minutes by default). Expiry ends the allocation, so a client mints the
  next credential before the current one expires and moves its relayed path to the new allocation.
- Every mint goes through the spend guard. It may refuse with `429` (the per-prefix mint limit), `403 ticket-limit`
  (the ticket has minted its share, or the hourly measurement blocked it for relaying far past its plan),
  `503 relay-unavailable` (relay off, kill switch engaged, or the day's relay budget spent) or `502 upstream-failed`. A
  failed mint costs the caller nothing.
- The kill switch revokes every live credential (ControlPlaneOperations.md, section 5.1). That includes a credential
  still being minted when it engages: the credential the provider returns is revoked without reaching the client, and
  the mint answers `503 relay-unavailable`. A mint that is waiting when an operator turns relay off answers the same
  way, even if relay is on again by the time the provider answers.
- Once the provider answers, the service also checks the caller again: that its ticket has not expired, that its
  admission holds, and that the hourly measurement has not blocked its ticket meanwhile. If any of that changed while
  the mint waited, the credential is revoked the same way, and the mint answers as a request made then would: `401`
  for a join ticket that expired or stopped holding (section 5.2) or a host token that no longer opens the session,
  `404` for a joiner whose session has ended, and `403 ticket-limit` for a blocked ticket. If the service cannot ask
  whether the admission holds, it hands nothing out and answers `500`, or `401` for a ticket that has also expired.

## 8. Signaling

`GET /v1/signal/{serverId}` with `Upgrade: websocket` and a host token or a join ticket for that server opens the
session's signaling room. The host keeps its socket open while it hosts; a peer's socket lasts at most fifteen minutes
or until its ticket expires. A newer socket for the same host or ticket replaces the older one.

Frames are canonical JSON text of at most 8 KiB. Binary frames close the socket. There is no free text in any frame.

### 8.1 What clients send

| From | Frame |
| --- | --- |
| Peer | `{"type":"offer","ice":{"pwd":…,"ufrag":…},"candidates":[candidate…]}` |
| Host | `{"type":"answer","peer":"<peer id>","ice":…,"candidates":[candidate…]}` |
| Either | `{"type":"candidate","candidate":candidate}` (the host adds `"peer"`) |
| Either | `{"type":"punch","delayMs":0-10000}`: start connectivity checks after the delay (the host adds `"peer"`) |
| Either | `{"type":"bye","reason":"done"|"cancelled"|"failed"|"timeout"}` (the host adds `"peer"`) |

- A candidate is `{"address":…,"kind":"host"|"srflx"|"relay","port":1-65535,"priority":0-4294967295}`. The address
  is an IP literal in its canonical text (dotted IPv4, RFC 5952 IPv6, a mapped address as IPv4).
- ICE credentials are `ufrag` of 4 to 32 and `pwd` of 22 to 64 characters from `A-Z a-z 0-9 + /`.
- An offer or answer carries at most 16 candidates.

### 8.2 What the service sends

| To | Frame |
| --- | --- |
| Host, on connecting | `{"type":"welcome","role":"host","peers":[{"peer","relayOnly"}…]}` |
| Peer, on connecting | `{"type":"welcome","role":"peer","peer":"<its id>","hostOnline":…,"hostRelayOnly":…}` |
| Host | `{"type":"joined","peer","relayOnly"}`, `{"type":"left","peer"}` |
| Peer | `{"type":"host-status","online","relayOnly"}` |
| Host | A peer's frame with `"peer":"<id>"` added |
| Peer | The host's frame with `peer` removed |
| Sender | `{"type":"notice","code":"candidates-withheld","withheld":N}` |
| Sender | `{"type":"error","code":"invalid-message"|"host-offline"|"unknown-peer"|"rate-limited"}` |

A peer id is 16 hexadecimal characters derived from its ticket. Peers never learn each other's ids.

`host-offline` and `unknown-peer` mean that a frame's counterpart has no open socket, so the frame went nowhere. A host's
`bye` is the exception: it is never answered with `unknown-peer`, because for any peer the room admitted it takes
effect without being delivered (section 8.4).

### 8.3 Privacy, enforced here

When a side is relay-only, the service forwards from it only candidates that are all of these:

1. of kind `relay`;
2. inside the deployment's relay address ranges, `RELAY_ADDRESS_RANGES`, which the operator must set to the ranges the
   relay provider allocates relay addresses from (ControlPlaneOperations.md, section 4.1);
3. not a local-only address (unspecified, loopback, private, shared CGNAT, link-local, unique-local, multicast or
   broadcast);
4. outside the sender's own scope as the service observed it on the socket's upgrade: not its IPv4 address, and not in
   its IPv6 /64. The comparison uses keyed hashes held only for the socket's life.

What that guarantees: whatever a relay-only side sends, and however it labels it, every candidate the other side
receives from it names an address inside `RELAY_ADDRESS_RANGES`, and none names the IPv4 address or an address in the
IPv6 /64 the side connected to the service from. Rule 4 still applies when that address is itself inside those ranges,
as it may be for a side that reaches the service through the relay provider's own network.

What it cannot guarantee: the service sees only the one address a side connects from. Its other addresses, such as
another interface's or those of the other address family, stay back only if they lie outside `RELAY_ADDRESS_RANGES`.
The guarantee is therefore only as narrow as that list: one wider than the provider's relay allocations lets through
any address of the side's that falls inside it, and nothing in the service can notice.

**The shipped list was checked on 2026-10-16.** The value in `services/control-plane/wrangler.toml` equals
Cloudflare's published address lists (https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6).
Realtime TURN's documentation names no narrower range for relay allocations, and its FAQ says it issues IPv4 relay
addresses only, so the IPv6 blocks never match a genuine relay candidate. The list is therefore wider than relay needs:
it covers every address Cloudflare publishes for its network. ControlPlaneOperations.md, section 4.1, says how to
repeat the check. The relay-only join in the first staging session catches a missing block,
but it cannot catch a list that is too wide: a wider list passes the same join without any sign. Even a missing block
shows only for the relay addresses that one join was given.

Anything withheld is reported to the sender in a `notice`; a trickled candidate that may not leave is dropped whole. A
direct side's candidates pass unchanged: privacy protects the side that chose it. Signaling is held in memory only and
never stored.

### 8.4 Limits and close codes

- **Places.** A room takes `maxPlayers + 2` peer sockets: a place for every joiner a full session has, and three more.
  One /24 or /56 may hold at most four of them, and never more than half, so no one network can fill a room. A
  joiner's socket counts until it closes, so a joiner closes it once its connection is up. A refused upgrade answers
  `409 room-full`.
- **Frames.** At most 40 per socket in any ten seconds.
- **A host's goodbye.** A `bye` from the host closes that peer's socket and keeps its ticket out of the room for as
  long as the ticket could open it: a later upgrade with it answers `401`. It does so whether or not the peer still has
  an open socket, because the room remembers every peer it admitted until that peer's ticket expires. A host may say
  goodbye after the peer's `left`, whether the peer closed its socket or the service closed it, and the ticket stays
  out all the same; such a goodbye has nothing to deliver and gets no answer. A goodbye for a peer id the room never
  admitted, or whose ticket has expired, changes nothing and gets no answer either. The player can come back only with
  a new ticket, which has a new peer id.
- **What the room remembers.** At most 1,024 goodbyes and 4,096 admitted peer ids at once, each until its ticket
  expires and never past the session's end. Only a new entry can find a list full, and then the entry whose ticket
  expires first makes way: a repeated goodbye, or the same ticket admitted again, never pushes another entry out. A
  goodbye that makes way lets its ticket back in until that ticket expires. Once a peer's admission has made way, a
  goodbye keeps the peer out only while the room still holds its socket.

| Code | Reason |
| --- | --- |
| 1000 | Goodbye, after a `bye` |
| 1003 | Binary frame |
| 1009 | Frame too large |
| 4400 | Five invalid frames |
| 4403 | The peer's ticket stopped holding: its invite was withdrawn or expired, or the server stopped being public |
| 4408 | Frame rate exceeded |
| 4409 | Replaced by a newer socket, or the host registered again |
| 4410 | The host session ended; a socket of a session that lapsed closes on its next frame |
| 4411 | The peer socket's lifetime ended |

An upgrade the service refuses answers with an ordinary error response (`400`, `401`, `404`, `409`, `429` or `503`).

## 9. Admin

Behind `Authorization: Bearer <ADMIN_TOKEN>`. Every authentication failure, including a deployment with no admin token,
is the same `401`. See ControlPlaneOperations.md for when to use each.

| Request | Effect |
| --- | --- |
| `GET /v1/admin/state` | Flags, kill switch, today's spend counters and limits |
| `PUT /v1/admin/kill-switch` `{"engaged":bool,"note":text|null}` | Engage (revoking every live credential) or release |
| `PUT /v1/admin/features` `{"hosting"?,"invites"?,"signaling"?,"relay"?}` | Turn features on or off; at least one member |
| `POST /v1/admin/servers/{serverId}/delist` `{"holdSeconds":0-2592000}` | End a session now and refuse its key for the hold |
| `POST /v1/admin/spend/measure` (no body) | Run the hourly relay usage measurement now |

## 10. Durable Objects

| Object | Instances | Holds |
| --- | --- | --- |
| `HostSession` | One per server id | The session with its random epoch, its invites, used challenge nonces, an operator hold, the peer ids its room admitted and those its host said goodbye to, and the signaling room in memory. Its alarm ends a lapsed session and deletes its storage, and closes the sockets of tickets whose invite has expired. |
| `DirectoryShard` | 16 per game build and protocol major, by the server id's first hex digit | Public listings, each with its expiry |
| `InviteShard` | 32, by the code's first character | Code to server id, with expiry |
| `SpendGuard` | One | Feature flags, the kill switch, per-ticket relay accounting, per-prefix mint windows, minted credential names, daily totals, and the latest usage measurement |
| `RateLimiter` | 64, by a keyed hash of the prefix | Sliding-window counts for every other limit |

The service keeps no raw IP address anywhere, and its invocation logs are off. Retention is listed in
ControlPlaneOperations.md.

## 11. Fixtures and tests

- `tests/fixtures/multiplayer/control-plane/exchanges.json` records a full host and join session over HTTP, and
  `signaling.json` a signaling transcript with relay-only stripping. Both are recorded by the service's own tests with
  a fixed clock and seeded randomness, and the tests fail if the wire format drifts. Clients replay them: a client must
  build each request body byte for byte and parse each response. Regenerate after a deliberate change with
  `npx vitest run -u test/client-fixtures.test.ts` in `services/control-plane`.
- The service replays the shared descriptor and link fixtures: every valid descriptor verifies and signs back to its
  served bytes, every invalid one is refused, and invite-code normalization matches.
- `npm test` there type-checks and runs every suite: auth and challenges, TTLs and heartbeats, alarms, invites and
  the uniform miss under concurrency, ticket scope (mints whose ticket stops holding or expires while they wait
  included), signaling and candidate stripping, the room's places, the host's goodbyes and what the room remembers for
  them, host sockets across registrations, the spend guard's caps and concurrency, the kill switch, turning relay off
  and a measurement blocking a ticket with mints in flight, the switch's revocation passes and its hourly trip, admin
  access, semantic versions (hostile ones read in bounded time), and a property test that no response and no stored
  byte carries an address.

## 12. Decisions recorded here

- **Canonical JSON everywhere.** Requests as well as responses, so a body has one meaning in every runtime and a
  signature covers exactly what was parsed.
- **The descriptor travels as a string.** The bytes the host signed reach the service unchanged and are served
  unchanged, so `descriptorSha256` pins the exact served bytes.
- **Invite resolution and withdrawal are POSTs with the code in the body,** so codes stay out of URLs and any request
  log.
- **Join tickets are stateless,** authenticated by a MAC, so relay minting and signaling need no ticket lookup. They
  carry the session's epoch and a keyed hash of their invite, so the session can refuse a ticket from an earlier
  session or a withdrawn invite without storing any ticket.
- **Relay-only is an allowlist.** A relay-only side forwards only addresses inside the deployment's relay address
  ranges, rather than everything except what the service can recognize as the side's own.
- **A fifth Durable Object, `RateLimiter`,** carries every per-prefix limit except relay minting, which the spend
  guard keeps itself as the plan says. Counting invite misses inside the invite shards would undercount by the number
  of shards.
- **The kill switch revokes live credentials,** so its effect is immediate instead of waiting out the credential TTL.
