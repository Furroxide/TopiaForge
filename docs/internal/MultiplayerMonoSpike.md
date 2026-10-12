# Live-multiplayer Mono spike: runtime measurements (P0) and two-instance overlays (S1)

Status: Spike record (throwaway code, nothing shipped) · Date: 2026-10-12 · Owner: multiplayer ·
Game: Robotopia build 2545

This document records two measurement spikes for the live-multiplayer plan:

- **P0** measured what the game's own Mono runtime can do for transport, security, timing and voice.
- **S1** checked whether two game processes can run at once on one Windows machine, each from its own
  overlay root.

All in-game numbers come from a throwaway BepInEx plugin running inside the game. Process-level
numbers came from PowerShell, outside the game. No IP address appears in this document or in the raw
results. Network interfaces and sockets are reported as counts, address kinds and port numbers only.

## Verdicts

- **S1: GO.** Two instances ran concurrently from overlay roots. Each root holds copied executables,
  junctions to `Robotopia_Data` and `MonoBleedingEdge`, and a private BepInEx. The second instance
  started while the first sat at the main menu. Both reached the menu, both loaded BepInEx, and both
  bound their own UDP socket on distinct ports. They exchanged loopback UDP with a p50 round trip of
  about 60 µs. The [recipe](#overlay-recipe-that-worked) is below.
- **P0: the managed stack is enough, with three socket workarounds.**
  - Map IPv4 destinations to IPv6 before sending on a dual-mode socket.
  - Turn off `SIO_UDP_CONNRESET`.
  - Do not rely on `Socket.DontFragment`.

  The class library has no working AEAD or elliptic-curve primitive, so the security layer must ship
  BouncyCastle. BouncyCastle 2.7.0 and Concentus 2.2.2 both load and run under this Mono.

## Headline numbers

The values come from P0 run 3. A range spans runs 1 to 3 where they differ.

| Measurement | Result |
| --- | --- |
| UDP loopback round trip, 64 B, cross-thread echo | p50 18.3 to 19.2 µs, p95 26 to 38 µs, 0 timeouts in 2,000 |
| UDP 1200-byte datagrams, one sender thread, 2 s | 189,000 to 200,000 packets/s (1.81 to 1.92 Gbit/s), loss at most 0.05 % |
| HTTPS GET of the Cloudflare trace, first / warm | 69 to 108 ms / 13 to 21 ms, TLS 1.2, HTTP/1.1 |
| WSS echo, echo.websocket.org | connect 119 to 124 ms, echo 22 to 30 ms |
| `Stopwatch` | 10 MHz, high resolution, 100 ns smallest step, about 22 ns per read |
| `Thread.Sleep(1)`, background thread | p50 1.48 to 1.50 ms, p95 2.06 to 2.50 ms |
| Class library AES-GCM, ChaCha20-Poly1305, ECDH, ECDsa | Not supported, absent, not implemented, not implemented |
| BouncyCastle X25519 agreement | about 12,000/s (82 µs) on either thread |
| BouncyCastle Ed25519 sign / verify, 64 B | about 23,500/s / about 12,500/s |
| BouncyCastle ChaCha20-Poly1305, 1200 B, background | seal 222 MB/s, open 201 MB/s, seal then open 105 MB/s (87,500 packet pairs/s) |
| Concentus encode, complexity 0 / 3 / 5 / 8 / 10 | mean 0.19 / 0.22 / 0.33 / 0.44 / 0.42 ms per 20 ms frame |
| Concentus decode | mean 0.06 to 0.09 ms per 20 ms frame |
| `Application.runInBackground` | `true` at Awake, Start, menu and end, in all 7 launches |
| Game-owned UDP sockets at the main menu | none (LiteNetLib is present but idle) |
| One instance at the menu | working set about 1.0 GB, private about 1.97 GB, 45 to 55 % of one core alone, 67 to 74 % while a second instance runs |

## Environment

| Item | Value |
| --- | --- |
| Game build | 2545: the official launcher's `installed-build.json` reads `{"id":2545,"dev":false}`, the main menu renders "Dev Version: v02545", and `filelist.json` has SHA-256 prefix `2172c255`. `Application.version` reports `0.1`. Build GUID: `790d019d72334ef39f6f534f3298d8cc`. |
| Unity | 6000.0.31f1 (a206c360e2a8), per `UnityPlayer.dll` and `Application.unityVersion`. Graphics device: Direct3D 11. Menu at 100 fps (`vSyncCount` 1 on a 100 Hz display, `targetFrameRate` -1). |
| Mono | `Mono 6.13.0 (Visual Studio built mono)` (`RuntimeInformation.FrameworkDescription`). `Environment.Version` is 4.0.30319.42000. mscorlib 4.0 class library with a netstandard 2.1 facade. |
| GC | Boehm (`mono-2.0-bdwgc.dll`), incremental with a 3 ms slice (`gc-max-time-slice=3` in `boot.config`), workstation, latency mode Interactive. |
| Thread pool | min 32 worker / 32 I/O, max 3,200 worker / 200 I/O. |
| TLS provider | `Mono.Unity.UnityTlsProvider` ("unitytls"). Supported protocols: `Tls, Tls11, Tls12`. `HttpClientHandler` delegates to `MonoWebRequestHandler`. |
| Loader | Vendored BepInEx 5.4.23.5 (`third_party/BepInEx/win_x64_5.4.23.5`), Doorstop proxy `winhttp.dll`, default config. |
| Host | Current 16-core (32-thread) desktop CPU, 32 GB, Windows 11 x64. |

Binary SHA-256 values, for reproduction:

| File | SHA-256 |
| --- | --- |
| `Robotopia.exe` | `c2a34751224673d9311ff363d249f3bddc6f049cd4eb4b8f09bee2172112fed2` |
| `UnityPlayer.dll` | `ded00cfa6d1d77f780168a569e1e93c9e399acfd99f8614521df9209e66e9f6e` |
| `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` | `ad2ea80b9718ef351befdaa64a8120ed1079f7c7629204e609382a0dcf46b3dd` |
| `Robotopia_Data/Managed/mscorlib.dll` | `1968f25b7f83e118d95c4baf96cfebbadbac324e8b773fcc2dcaea194e4be5cb` |
| `Robotopia_Data/Managed/System.dll` | `814c7986f9f552c9d8acd0ca3a8a76482a3a79b791121f487450f8418baac7a6` |
| `Robotopia_Data/Managed/ThirdParty.dll` | `975140674bb56c10c4785a4381a248e0e5484c2682f4d22166eb23c49426d6a7` |
| `Robotopia_Data/Managed/GameCode.dll` | `79f57da1075e40afe9c9e52991d9bdeca0d07780a4c414e93132f932c6023672` |
| BepInEx `winhttp.dll` | `8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c` |
| BepInEx `core/BepInEx.dll` | `8255b28902886085c578b9e427d3073c97002db85176d2090cdeda90ef14ce70` |
| `BouncyCastle.Cryptography.dll` (2.7.0, netstandard2.0) | `d61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495` |
| `Concentus.dll` (2.2.2, netstandard2.0) | `11a6de9cbf2ccf27fe359c4847ba63d8ed593284b97ef884e0dc2e9dc7583a01` |
| `System.Numerics.Vectors.dll` (4.5.0, netstandard2.0) | `17924e5dc87e0d6229d2dd0bcfc1fdfabd820901b13a68baa89fcb80c4d1a67f` |

## Method

**Plugin.** The plugin was a `BaseUnityPlugin` targeting netstandard2.1. It was compiled against the
game's own `UnityEngine.dll`, `UnityEngine.CoreModule.dll` and `UnityEngine.ScreenCaptureModule.dll`,
and against the vendored `BepInEx.dll`. NuGet packages: `BouncyCastle.Cryptography` 2.7.0 (the latest
stable 2.x on the day) and `Concentus` 2.2.2. Only their netstandard2.0 DLLs were copied beside the
plugin, plus `System.Numerics.Vectors.dll` in the P0 runs. The game's own `System.Memory`,
`System.Buffers` and `System.Runtime.CompilerServices.Unsafe` were used.

**Sequence inside the game:**

1. Read `runInBackground` in Awake and in Start.
2. Wait for the start-menu scene (`TestCityStartMenu`, build index 0). Then wait until 10 s and 60
   frames have passed with no further scene load.
3. Read `runInBackground` again, take Unity and environment snapshots, scan for LiteNetLib, and save a
   screenshot.
4. Hold a 30 s idle window that records frame times. The external sampler measures the process during
   this window.
5. Run the tests:
   - Background thread: interfaces, UDP, clocks, class-library crypto, then TLS.
   - Unity main thread: BouncyCastle, one benchmark per frame, each with 0.25 s warm-up and 1.0 s
     timed.
   - Background thread: the same BouncyCastle benchmarks.
   - Background thread: Concentus.
6. Write a JSON results file next to the plugin after each stage, and log every section to
   `BepInEx/LogOutput.log`.

**External sampler.** A PowerShell runner launched the overlay and followed the plugin's phase file.
At the menu it recorded:

- `Get-NetUDPEndpoint` and `Get-NetTCPConnection` filtered by the launched PID: ports and bind kind
  only.
- Working set, private bytes, threads and handles.
- CPU time over a 10 s window.

It always stopped the processes it had launched, by PID.

**Runs.** P0 ran three times, single instance, launch to finish in about 80 s each:

- Run 1 exposed the dual-mode send failure described below. Runs 2 and 3 map IPv4 destinations.
- Run 2's external sample fired at launch because a stale phase file was left from run 1. It is
  discarded.

S1 ran twice with two instances. Run 1's external sample was lost to a runner error while stopping
an already exited child process; its in-game results are complete. Run 2 is complete. Seven game
launches in total.

**Privacy.** Exception messages pass through a redactor before logging. Response bodies are never
stored. A final scan matched every produced file against the host's own unicast addresses: 0 hits.
The only dotted-quad strings in the outputs are assembly version numbers.

## P0 results

### 1. UDP sockets

**Method.** Two `Socket(InterNetworkV6, Dgram, Udp)` objects, with `DualMode = true`, were bound to
the IPv6 any-address with port 0:

- an echo server, with a background `ReceiveFrom` loop;
- a client that sends to the IPv4 loopback and the IPv6 loopback.

The run then tried `DontFragment` on dual-mode, IPv6-only and IPv4 sockets, and read the result back
through `ws2_32!getsockopt`. It also triggered an ICMP port-unreachable with and without
`SIO_UDP_CONNRESET`. It timed 2,000 ping-pongs per path after 100 warm-up exchanges. Finally it
blasted 1200-byte datagrams for 2 s into a sink thread.

**Results.**

- **Dual mode works for receiving and replying.**
  - A new IPv6 datagram socket already reports `DualMode = true` in this Mono, unlike .NET. An
    IPv6-only socket must clear it explicitly.
  - An IPv4 sender's datagram arrives as an IPv4-mapped IPv6 endpoint. Replying to that endpoint
    reaches the IPv4 socket.
- **Sending from a dual-mode socket to a plain IPv4 `IPEndPoint` fails** with `SocketException`
  10014 (WSAEFAULT, "invalid pointer address"). The same destination works after
  `IPAddress.MapToIPv6()`. Mono does not do the mapping that .NET does.
- **`DontFragment`:**
  - On dual-mode and IPv6-only sockets, the getter and the setter both throw `NotSupportedException`
    ("This protocol version is not supported").
  - On an IPv4 socket the setter does not throw, but it does nothing: native `IP_DONTFRAGMENT` still
    reads 0 and the managed getter returns `false`.
  - `SetSocketOption(IP, DontFragment, true)` also returns without effect. At the IPv6 level it
    throws 10042 (WSAENOPROTOOPT).
  - A direct `ws2_32!setsockopt` P/Invoke works. It sets `IP_DONTFRAGMENT` and `IPV6_DONTFRAG` to 1
    on a dual-mode socket, verified by `getsockopt`.
- **Connection reset:** without the ioctl, a datagram to a closed loopback port makes the next
  `ReceiveFrom` throw `SocketException` ConnectionReset (10054). The socket stays usable afterwards.
  `Socket.IOControl(SIO_UDP_CONNRESET, false)` succeeds in this Mono and removes the reset.
- **Socket buffers:** 64 KiB send and receive by default. Setting a 4 MiB receive buffer is accepted.

**Round trip in microseconds,** 64-byte payload, 2,000 samples, runs 2 and 3:

| Path | p50 | p95 | p99 | max |
| --- | --- | --- | --- | --- |
| Dual-mode client to IPv4 loopback (mapped) | 18.9 to 19.2 | 33.9 to 37.5 | 64 to 80 | 3,470 to 4,160 |
| Dual-mode client to IPv6 loopback | 18.6 to 19.2 | 26.6 to 27.6 | 64 to 66 | 109 to 127 |
| IPv4-only client to IPv4 loopback | 18.3 to 18.8 | 26.3 to 27.9 | 57 to 58 | 1,876 to 2,091 |

**Throughput,** 1200-byte datagrams, one sender thread, 2 s, runs 2 and 3:

| Path and sink buffer | Received packets/s | Received Mbit/s | Loss |
| --- | --- | --- | --- |
| IPv4 loopback (mapped), 64 KiB | 188,865 to 199,980 | 1,813 to 1,920 | 0.04 to 0.05 % |
| IPv4 loopback (mapped), 4 MiB | 194,072 to 197,617 | 1,863 to 1,897 | 0 % |
| IPv6 loopback, 64 KiB | 198,436 to 199,927 | 1,905 to 1,919 | 0 % |
| IPv6 loopback, 4 MiB | 196,857 to 199,589 | 1,890 to 1,916 | 0 % |

**Network interfaces.** `NetworkInterface.GetAllNetworkInterfaces()` works and takes 6.5 to 6.9 ms
per call. It reported:

- 6 interfaces, 2 of them up: 1 Ethernet and 1 loopback.
- Unicast addresses on the up interfaces: IPv4 loopback 1, IPv4 private 1, IPv6 link-local 1, IPv6
  loopback 1.
- No global or ULA IPv6, and no random-suffix (temporary) IPv6 addresses.
- `SuffixOrigin` and `GetIsNetworkAvailable` both work.

**Implications:**

- Use one dual-mode socket per peer on a dedicated network thread. Normalise every IPv4 destination
  with `MapToIPv6()` before `SendTo`, and set `SIO_UDP_CONNRESET` to false at creation.
- Managed UDP is not a bottleneck. One thread moves about 190,000 datagrams/s, while a 32-peer
  session at 256 kbit/s per peer needs about 850.
- Path-MTU work cannot use `Socket.DontFragment`. Keep datagrams at or below 1200 bytes, or add a
  per-OS `setsockopt` shim.
- Enumerate interfaces off the main thread and cache the result. This host has only a private IPv4
  and a link-local IPv6, so internet play from it needs NAT traversal or a relay.

### 2. TLS, HTTP and WebSockets

**Method.** One `HttpClient` with the default handler and a 20 s timeout fetched each Cloudflare
trace endpoint twice:

- `https://www.cloudflare.com/cdn-cgi/trace`;
- the same path on Cloudflare's anycast resolver IP-literal host.

No headers were added. From each response the plugin kept only:

- status, latency, and the `server` header;
- whether `cf-ray` is present;
- whether the body parsed as key=value lines with an `ip=` line, and the body's `tls=`, `http=` and
  `sni=` values.

`ClientWebSocket` then connected to each echo server and timed two echo round trips. The netstandard
2.1 `ClientWebSocket` API exposes no handshake response headers, so each echo host was also fetched
once over HTTPS to read its headers.

**Results.** No exceptions in any run.

- **Cloudflare by hostname:** 200, `server: cloudflare`, `cf-ray` present, `ip=` line present.
  The body reports `tls=TLSv1.2`, `http=http/1.1`, `sni=plaintext`. First request 83 to 108 ms, warm
  request 16 to 20 ms.
- **Cloudflare IP-literal host:** the same results. Certificate validation passed for the IP literal.
  First request 69 to 84 ms, warm request 13 to 21 ms.
- **`wss://echo.websocket.org/`:** works. Connect 119 to 124 ms, echo 22 to 30 ms. The server sends
  one greeting message before echoing. It is not Cloudflare-fronted: the response carries
  `server: Fly/...`, a `via` header and `fly-request-id`, and no `cf-ray`.
- **`wss://ws.postman-echo.com/raw`:** works. Connect 443 to 458 ms, echo 103 to 112 ms. HTTPS GET
  returns 404 with `server: nginx` and no `cf-ray`, so it is not visibly Cloudflare-fronted either.

**Implication.** Plan on TLS 1.2 and HTTP/1.1 only: UnityTLS lists no TLS 1.3, and every request went
out as HTTP/1.1. Do not plan QUIC or HTTP/3 through it. WSS through `ClientWebSocket` works for
signalling or a relay fallback, at about 120 ms per connection and the path RTT per message.

### 3. Clocks and sleeping

**Method.** On a background thread:

- read `Stopwatch.Frequency` and `IsHighResolution`;
- read `GetTimestamp()` in a tight loop for 0.5 s;
- time 200 calls of `Thread.Sleep(1)`;
- find the smallest `DateTime.UtcNow` step over 0.2 s.

**Results.**

- `Stopwatch` runs at 10,000,000 Hz (100 ns ticks), and `IsHighResolution` is `true`.
- The smallest non-zero step between reads is 1 tick (100 ns). Each read costs about 22 ns. About 78
  % of consecutive reads return the same tick.
- `Thread.Sleep(1)` over 200 samples: p50 1.48 to 1.50 ms, p95 2.06 to 2.50 ms, min 0.53 to 0.98 ms,
  max 2.61 to 2.73 ms, mean 1.37 to 1.40 ms. The game process therefore runs at about 1 ms timer
  resolution.
- `DateTime.UtcNow` advances in steps of 0.3 to 0.5 ms.

**Implication.** Use `Stopwatch` for RTT, jitter and the tick clock; it resolves 100 ns. A network
thread can pace itself at 1 to 2 ms with `Sleep(1)`. Anything finer needs spinning or blocking
receives.

### 4. Cryptography

#### 4a. Class library availability

The probe searched for each type by name across the loaded assemblies, then constructed it and used
it.

| Primitive | Result |
| --- | --- |
| `AesGcm` | Type exists in mscorlib. The constructor throws `PlatformNotSupportedException`. |
| `ChaCha20Poly1305` | Type not present. |
| `ECDiffieHellman.Create()` | `NotImplementedException` |
| `ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)` | `NotImplementedException` |
| `ECCurve.CreateFromFriendlyName("curve25519")` | Returns a named curve with no OID value. |
| `ECDiffieHellman.Create(...)` for `curve25519`, `x25519` and the X25519 OID | `NotImplementedException` |
| `ECDsa.Create()` | `NotImplementedException` |
| `RandomNumberGenerator.Create()` | Works (`RNGCryptoServiceProvider`). |
| `HMACSHA256` | Works. |

Apart from that `AesGcm`, no loaded game or framework assembly carries a Gcm, ChaCha, Poly1305,
Salsa20 or 25519 type.

**Implication.** Every modern primitive the security layer needs (AEAD, key agreement and
signatures) must come from a managed library shipped with TopiaForge. The class library supplies only
randomness and hashing.

#### 4b. BouncyCastle 2.7.0 throughput

**Method.** `BouncyCastle.Cryptography` 2.7.0, netstandard2.0 build (assembly version 2.0, no
dependencies). Each benchmark ran with 0.25 s warm-up and 1.0 s timed, first on the Unity main thread
(one benchmark per frame) and then on a dedicated background thread.

- ChaCha20-Poly1305 used a 12-byte nonce counter, 16 bytes of AAD and a 16-byte tag, with
  `AeadParameters` built per packet.
- Ed25519 signed and verified a 64-byte message.

**Run 3, operations per second:**

| Operation | Main thread | Background | Runs 1 to 3, both threads |
| --- | --- | --- | --- |
| X25519 agreement | 12,430 (80 µs) | 12,061 (83 µs) | 10,956 to 12,530 |
| X25519 key generation | 25,716 | 24,367 | 24,367 to 25,833 |
| Ed25519 sign, 64 B | 23,948 | 23,566 | 23,174 to 24,191 |
| Ed25519 verify, 64 B | 13,345 | 12,496 | 10,714 to 13,345 |
| ChaCha20-Poly1305 seal, 1200 B | 190,878 (229 MB/s) | 185,320 (222 MB/s) | 213 to 231 MB/s |
| ChaCha20-Poly1305 open, 1200 B | 170,272 (204 MB/s) | 167,204 (201 MB/s) | 199 to 206 MB/s |
| Seal then open, 1200 B | 90,008 pairs (108 MB/s) | 87,552 pairs (105 MB/s) | 105 to 110 MB/s |

**Observations:**

- The main thread and the background thread agree within about 5 %. The exception is one background
  verify reading in run 2, 17 % lower.
- The per-packet parameter objects caused 17 to 23 Boehm collections in each 1 s ChaCha20 benchmark.
  That rate is negligible at session packet rates, but a production wrapper should cache them.

**Implications:**

- BouncyCastle ChaCha20-Poly1305 seals and opens at about 105 MB/s on one network thread. That
  supports about 3,300 full-duplex connections at 256 kbit/s each way. Each such connection is about
  27 packet pairs/s, or about 0.03 % of a core, so AEAD will not be the bottleneck.
- A full handshake side costs about 0.25 ms: X25519 key generation, agreement, Ed25519 sign and
  Ed25519 verify. That is about 4,000 handshakes/s per thread. On the main thread it would take 2.5
  % of a 10 ms frame, so keep handshakes on the network thread.

### 5. Concentus (managed Opus)

**Method.**

- **Encoder settings:** `Concentus` 2.2.2, 48 kHz mono, `OPUS_APPLICATION_VOIP`, 20 ms frames (960
  samples). `Bitrate` 24000, `UseVBR` true, `UseConstrainedVBR` false, `UseInbandFEC` true. FEC only
  engages with a loss hint, so `PacketLossPercent` was 10. `UseDTX` true, `SignalType` voice.
- **Managed codec:** forced with `OpusCodecFactory.AttemptToUseNativeLibrary = false`. The default
  factory also returned the managed encoder, because there is no native opus library.
- **Test signal:** a synthetic voiced signal, continuous with no silent gaps. It is a harmonic series
  of a 100 to 170 Hz fundamental, shaped by four formant resonances under a 3.7 Hz syllable envelope,
  plus white noise.
- **Timing:** 50 warm-up frames discarded, then 600 frames (12 s of audio) per complexity, timed per
  frame on a background thread.

**Run 3, milliseconds per 20 ms frame:**

| Complexity | Encode mean | Encode p95 | Encode p99 | Decode mean | Decode p95 | Mean packet | Effective rate |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | 0.192 | 0.184 | 1.86 | 0.066 | 0.068 | 60.4 B | 24.2 kbit/s |
| 3 | 0.220 | 0.212 | 1.90 | 0.065 | 0.062 | 59.0 B | 23.6 kbit/s |
| 5 | 0.329 | 0.394 | 2.33 | 0.070 | 0.087 | 59.8 B | 23.9 kbit/s |
| 8 | 0.442 | 0.501 | 2.59 | 0.083 | 0.096 | 60.2 B | 24.1 kbit/s |
| 10 | 0.421 | 0.469 | 2.19 | 0.094 | 0.122 | 60.2 B | 24.1 kbit/s |

**Variation across runs 1 to 3:**

- Encode mean: complexity 0 from 0.169 to 0.194 ms, 5 from 0.295 to 0.329 ms, 10 from 0.421 to 0.435
  ms.
- Decode mean: 0.062 to 0.094 ms.
- Isolated maxima of 2.4 to 9.5 ms appear at every level. They are GC and scheduler spikes, not codec
  cost.

**Output and loading:**

- Complexity 8 and 10 produced identical packets, so at this bitrate they are the same SILK setting.
- Every frame decoded to 960 samples. The continuous signal produced no DTX frames.
- Concentus 2.2.2 references `System.Memory` from the 4.5.4 package, which binds to the game's higher
  versioned facade (assembly version 4.0.99). It also references `System.Numerics.Vectors`, which the
  game does not ship.
- The P0 runs shipped NuGet's `System.Numerics.Vectors` beside the plugin. The S1 runs shipped only
  `Concentus.dll`, and the codec still loaded and encoded, so this VOIP path never touches
  `Vector<T>`.
- The 1.1.7 fallback was not needed.

**Implication.** Complexity 10 costs about 0.43 ms to encode and 0.07 to 0.09 ms to decode per 20 ms
frame, and complexity 8 measured the same. Pick 10 as the default.

- Encoding takes about 2 % of one core per local talker.
- A 16-player session costs about 1.5 to 1.8 ms per 20 ms on one voice thread: one encode plus 15
  decodes.
- If encode p95 exceeds a budget on slow hardware, fall back to complexity 5 (about 0.31 ms).
- Keep voice coding off the main thread and keep at least one frame of jitter buffer, because p99 is
  about 2 ms at every complexity.

### 6. Application.runInBackground

**Method.** The plugin read the value in Awake, in Start, after the menu settled, and at the end. It
also polled the value every frame to catch any change by the game.

**Results.** The value was `true` at every reading in all seven launches, including both S1
instances, and the game never changed it.

**Other display and focus observations:**

- `Screen.fullScreenMode` was `Windowed` at 304x201 in Awake. By the menu it was `FullScreenWindow`
  at 960x540, despite `-screen-fullscreen 0`. The game applies its own saved display mode; see S1.
- Each instance logged one focus-gained event and never a focus-lost event, even when the second
  instance opened.

**Implication.** Peers keep simulating and ticking while unfocused, so no override is needed. Setting
it explicitly in the runtime costs nothing as a guard against a future game change.

### 7. Embedded LiteNetLib and the game's own sockets

**Method.**

- **Inside the game:** scan every loaded assembly for type names containing `LiteNetLib` or in the
  `FlyingWormConsole3` namespace. For each Unity object type found, count live instances with
  `FindObjectsByType`, including inactive objects.
- **Outside the game:** at the menu, run `Get-NetUDPEndpoint -OwningProcess <pid>` and
  `Get-NetTCPConnection -OwningProcess <pid>`. These samples were taken before the plugin opened any
  socket.

**Results.**

- LiteNetLib is compiled into `ThirdParty.dll` as `FlyingWormConsole3.LiteNetLib`, Console Pro's
  remote-log transport. That is 118 LiteNetLib types among 119 `FlyingWormConsole3` types, including
  `ConsoleProRemoteServer`, `NetManager`, `NetPeer` and `NetSocket`.
- There are zero `ConsoleProRemoteServer` instances at the menu.
- At the menu in every sampled launch (P0 runs 1 and 3, and both S1 run 2 instances), the game process
  owns no UDP endpoints and no TCP listeners. It holds 5 established TCP connections (HTTPS for
  telemetry and sign-in).

**Implication.** The game neither binds nor reserves a UDP port, so there is no port contention. The
Console Pro copy is namespaced, so an upstream `LiteNetLib` shipped by TopiaForge would not clash on
type names.

## S1: two concurrent instances from overlay roots

### Verdict: GO

The second instance started while the first sat at the main menu. Unity did not refuse it as a
single instance, and no launcher or auth check got in the way. Both instances:

- reached the main menu;
- loaded BepInEx, each writing its own `BepInEx/LogOutput.log`;
- bound distinct UDP ports;
- stayed responsive until stopped.

### Overlay recipe that worked

`<game>` is the install that the official launcher's `launcher-config.json` (`game_dir`) points to,
plus `\Robotopia`. `<bep>` is `third_party\BepInEx\win_x64_5.4.23.5`. Repeat the steps for each peer
with its own root.

```powershell
$game = '<game>'; $bep = '<bep>'; $root = '<overlay root for this peer>'
New-Item -ItemType Directory -Force $root, "$root\D3D12", "$root\BepInEx\core", "$root\BepInEx\plugins" | Out-Null
'Robotopia.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'crashpad_handler.exe', 'crashpad_wer.dll' |
    ForEach-Object { Copy-Item "$game\$_" "$root\$_" }
Copy-Item "$game\D3D12\D3D12Core.dll" "$root\D3D12\"
New-Item -ItemType Junction -Path "$root\Robotopia_Data" -Target "$game\Robotopia_Data" | Out-Null
New-Item -ItemType Junction -Path "$root\MonoBleedingEdge" -Target "$game\MonoBleedingEdge" | Out-Null
'winhttp.dll', 'doorstop_config.ini', '.doorstop_version' | ForEach-Object { Copy-Item "$bep\$_" "$root\$_" }
Copy-Item "$bep\BepInEx\core\*" "$root\BepInEx\core\"
# Plugins go in $root\BepInEx\plugins\<name>\. Launch with the overlay root as the working directory:
Start-Process "$root\Robotopia.exe" -WorkingDirectory $root -ArgumentList '-screen-fullscreen', '0',
    '-screen-width', '960', '-screen-height', '540', '-logFile', "$root\player.log"
```

Notes on the recipe:

- `doorstop_config.ini` is used unchanged. Its relative `target_assembly` resolves inside the overlay,
  and BepInEx reports the overlay as its game root.
- `Paths.ManagedPath` resolves through the junction, and the BepInEx config, cache and logs stay in
  the overlay.
- Each overlay holds 48 MB of real files. The 5.4 GB data folder is shared through the junction.
- Remove a junction without touching its target with `[System.IO.Directory]::Delete('<link>', $false)`.
- The real install was never modified. Its root listing was identical before and after, and it still
  has no `winhttp.dll`.

### Observations (S1 run 2)

| | peer-0 | peer-1 |
| --- | --- | --- |
| Launched at | 0.1 s | 14.2 s (peer-0 already at the menu) |
| Start-menu scene loaded after process start | 2.2 s | 3.2 s |
| Menu settled (plugin clock, includes the 10 s settle) | 11.3 s | 11.4 s |
| BepInEx 5.4.23.5 chainloader and spike plugin | loaded | loaded |
| `runInBackground` | `true` | `true` |
| Game-owned UDP endpoints at the menu | none | none |
| Spike link socket (dual mode, IPv6 any-address) | port 45628 | port 11260 |
| Working set / private bytes at the menu | 1,016 / 1,976 MB | 1,008 / 1,959 MB |
| CPU over 10 s at the menu, % of one core (% of machine) | 73.8 (2.31) | 66.7 (2.08) |
| Threads / handles | 257 / 1,816 | 256 / 1,789 |
| Menu frame time in the idle window, p50 / p95 / max | 10.0 / 10.13 / 355 ms | 10.0 / 10.36 / 414 ms |
| Cross-process RTT to the other peer, IPv4 mapped, p50 / p95 / max | 65.5 / 150.9 / 302 µs | 64.1 / 121.6 / 325 µs |
| Cross-process RTT to the other peer, IPv6, p50 / p95 / max | 63.1 / 145.7 / 525 µs | 60.8 / 118.1 / 476 µs |

Notes on the table:

- **Single-instance baseline:** in P0 runs 1 and 3 one instance at the menu used 1,008 to 1,011 MB of
  working set, 1,966 to 1,967 MB private, 44.8 to 54.6 % of one core (1.40 to 1.71 % of the machine)
  and 259 to 260 threads.
- **CPU:** peer-0's figure was taken while its link loop was waiting with 2 ms polls.
- **Frame time:** peer-0's idle window overlapped peer-1's launch and load with no visible frame cost.
- **Cross-process RTT:** 300 samples per family in each direction. S1 run 1 gave p50 55.6 to 56.3 µs
  and p95 117.9 to 149.0 µs.
- **Child processes:** each instance starts `UnityCrashHandler64.exe` and two `crashpad_handler.exe`
  from its overlay. All exited when the game process was stopped.

**Implication.** One machine can host two modded peers for development and loopback tests at about 2
GB private memory and under 1 core each.

### Shared state and side effects

- **Per-user state is shared.** Both instances use the same persistent data folder
  (`%USERPROFILE%\AppData\LocalLow\Tomato Cake\` plus the game's product name), the same sign-in and
  the same PlayerPrefs key under `HKCU\Software\Tomato Cake\`.
  - The game rewrites `player_data.json.gz` at every launch. Its bytes were identical at each check
    (after the first launch, before S1, and after the last launch), and it is valid gzip and JSON
    afterwards.
  - `robo_token.json` was rewritten once, at the first launch, when the game refreshed its sign-in.
    Later launches logged that the token was recent enough.
  - The second concurrent instance logged a Sentry warning that it could not take the config
    directory lock file. Sentry's .NET event cache is therefore off in that instance. That was the
    only concurrency message.
  - Going past the main menu with two local peers (saves, settings, sign-in refresh) needs separate
    Windows accounts, a VM, or a persistent-data redirect.
- **Display arguments are partly ignored and partly persisted.** The game re-applies its saved
  `FullScreenWindow` mode, so both instances render borderless at 960x540 on the same display.
  - The size arguments overwrote the user's saved `Screenmanager Resolution Width/Height` and
    `Use Native` values, and added `Screenmanager Resolution Window Width/Height`.
  - Those values were restored to their pre-spike state afterwards. A rig must snapshot and restore
    the `Screenmanager*` values, or launch without size arguments.
- **Firewall.** No Windows Firewall rule exists for the overlay executables, and every spike packet
  was loopback, which needs none. Rules are per program path, so cross-machine tests need an inbound
  rule for each overlay `Robotopia.exe`.
- **Telemetry.** Each instance starts its own Sentry and PostHog sessions. Sentry answered 429
  (quota) during these runs. The game's own startup `NullReferenceException`s, which also appear in
  its unmodded log, go through Sentry's log handler.

### Direct launch without the official launcher

The game runs without the official Tomato Cake launcher. Every launch here started `Robotopia.exe`
directly, with no launcher process running and only Unity's standard screen and log arguments.

- The launcher's log line for a launch reads "launching game from <game_dir>
  (close_after_launch=true, open_url_present=false)". No token appears in it.
- No command-line or environment token is needed. The game signs in from its own token file in
  persistent data.
- Doorstop added its own `DOORSTOP_*` variables. The runner set nothing else for the launch.

## Not measured

- **Allocation per receive.** The managed garbage created per `ReceiveFrom` call was not quantified.
- **Real network paths.** Every packet was loopback, so the spike did not measure NAT behaviour, the
  on-the-wire effect of DF (loopback has no small MTU), or path latency other than the HTTPS and WSS
  probes.
- **Firewall prompt.** Whether Windows showed an allow-access prompt for the any-address bind was not
  observed.
- **Linux or Proton.** No run was made under Linux or Proton.
- **Main-thread sleep.** `Thread.Sleep` granularity on the Unity main thread was not measured.
- **Opus quality.** Only codec cost was measured, not quality at each complexity.
- **Gameplay with two instances.** Only the main menu was exercised. Neither instance entered a
  world.

## Raw data

Raw JSON results, BepInEx and player logs, screenshots, plugin source and runner scripts were kept
with the spike outside the repository. The results files:

| File | Content |
| --- | --- |
| `results/p0-run1/results-peer-0-full-20261012T164439Z.json` | P0 run 1, including the unmapped-send failures |
| `results/p0-run1/external-p0-run1.json` | P0 run 1, process sample at the menu |
| `results/p0-run2/results-peer-0-full-20261012T164947Z.json` | P0 run 2 |
| `results/p0-run3/results-peer-0-full-20261012T165308Z.json` | P0 run 3, the reference values |
| `results/p0-run3/external-p0-run3.json` | P0 run 3, process sample at the menu |
| `results/s1-run1/results-peer-{0,1}-s1-*.json` | S1 run 1, both peers |
| `results/s1-run2/results-peer-{0,1}-s1-*.json` | S1 run 2, both peers |
| `results/s1-run2/external-s1-run2.json` | S1 run 2, process samples for both peers |

The two overlay roots were kept outside the repository, with junctions intact, for reuse.
