# Multiplayer voice — design record

Status: Implementing (WS2g of the live-multiplayer plan) · Date: 2026-10-13 · Owner: multiplayer

This records voice chat below the user interface: the codec the provider runs, and the session engine's path for voice
frames between peers. Microphone capture, resampling, voice activity detection, playback and the voice worker thread
come in a later slice, as does every in-game control. No player-facing behaviour changes until then.

WS2g lands in two pull requests, each with its own part of this record:

1. **The codec** (part 1): Opus through vendored Concentus, wrapped for the provider.
2. **The engine's voice path** (part 2): the codec seam, the frame format, the jitter buffer and the host's relay
   policy.

## Part 1: the codec

The code lives in `mods/TopiaForge.Multiplayer/Voice/`, inside the provider only, and every type is internal. The
engine never names a codec; the provider hands it one through the seam in part 2.

### 1.1 Settings

| Setting | Value |
| --- | --- |
| Format | 48 kHz mono, 16-bit samples |
| Frame | 20 ms, 960 samples, one Opus frame per packet |
| Application | VOIP, signal type voice |
| Bitrate | Unconstrained VBR, 24 kbit/s target by default, 20 to 24 kbit/s allowed |
| Audio band | Capped at wideband (8 kHz), so the encoder stays in SILK-only mode |
| Silence | Discontinuous transmission: TOC-only packets after a hangover |
| Loss | In-band FEC, sized by an expected-loss hint (10% by default, 0 turns it off) |
| Complexity | 10 by default, 5 to 10 allowed |
| Packet | At most 160 bytes |

`OpusVoiceCodec` holds the settings, validates packets, and creates one `OpusVoiceEncoder` per talker and one
`OpusVoiceDecoder` per speaker. Encoders and decoders belong to one thread each. Complexity, bitrate and the loss hint
can change live on an encoder; the voice service will feed the hint from the loss its transport measures.

### 1.2 Why wideband

At 20 to 24 kbit/s the encoder picks fullband hybrid mode (TOC configuration 15) unless capped. Hybrid splits the
bits between SILK and CELT, and SILK's share stays under the threshold at which it codes in-band FEC (LBRR) at all.
Measured on the synthetic speech the tests use, 24 kbit/s, complexity 10, on .NET 10:

| Mode | Loss hint | Frames FEC improved | FEC / PLC SNR on lost frames | Encode | Decode |
| --- | --- | --- | --- | --- | --- |
| Fullband hybrid | 10% or 20% | 0 of 20 | 1.9 / 1.9 dB | 0.76 ms, 134 KB | 0.16 ms, 26 KB |
| Fullband hybrid | 25% | 13 of 20 | 4.0 / 1.6 dB, and 6.4 dB round trip | 0.80 ms, 143 KB | 0.15 ms, 27 KB |
| Wideband SILK | 10% | 13 of 20 | 5.4 / 1.9 dB, and 9.0 dB round trip | 0.68 ms, 104 KB | 0.10 ms, 12 KB |

So below a 25% hint, fullband FEC is a no-op, and buying it at 25% costs the clean stream 3 dB. Wideband keeps FEC
working at the default hint, halves decode time and allocation, and covers the speech band (up to 8 kHz). The sizes
are allocation per frame inside Concentus.

### 1.3 Vendored Concentus

Concentus 2.2.2 is a C# port of libopus 1.1.2 (fixed point) and of the Speex resampler. Its NuGet package names
repository commit `6c2328dc19044601e33a9c11628b8d60e1f3011c`; the files were fetched from GitHub at that commit, each
checked against its git blob hash, and compiled into the provider as source.

- **Namespace and visibility.** `Concentus` became `TopiaForge.Multiplayer.ThirdParty.Concentus` and every top-level
  type is internal, so this copy cannot collide with another Concentus in the game; Mono has no binding redirects.
- **Subset.** 122 of 149 files. Left out: the native-library interop (`Native/`) and the two factories whose only
  addition is choosing a native libopus, the multistream codec and its layouts, the quality-comparison tool, and two
  unused definitions. The set is minimal: removing any remaining file breaks the build.
- **No SIMD.** netstandard2.1 includes `System.Numerics.Vector<T>`, which the game's Mono forwards to a
  `System.Numerics.Vectors` it does not ship (`MultiplayerMonoSpike.md`, section 5). The three `Vector<T>` paths are
  removed and the scalar code each duplicated remains, so there is nothing to fail at JIT time, and every platform
  computes the same bytes.
- **`PARITY`.** Upstream's project defines it for every build; it selects the bit-exact fixed-point helpers over
  floating-point approximations. The two files that test it define it themselves.
- **One upstream defect fixed.** libopus treats a payload of one byte or less as a lost frame by setting `data` to
  null. The port keeps a separate offset into the payload and did not reset it, so once a decoder had decoded audio,
  every discontinuous-transmission packet threw `ArgumentOutOfRangeException`. `opus_decode_frame` now resets the
  offset with the payload.

`mods/TopiaForge.Multiplayer/THIRD_PARTY_NOTICES.md`, which ships in the provider package, lists every change and
every file with its upstream blob hash, and quotes every copyright line the files carry with both licence texts:
the Opus BSD licence for the codec and the Speex BSD licence for the resampler.

### 1.4 Packets from the network

`OpusToc` checks the first byte of a packet (RFC 6716, section 3.1) before anything decodes it. A voice packet is 1 to
160 bytes, mono, holds one frame (code 0), and uses one of the nine configurations whose frames last 20 ms: SILK
narrow, medium and wide band, hybrid super-wide and full band, and CELT narrow, wide, super-wide and full band. A code-0
packet carries no length fields, so this check bounds the work: whatever follows the TOC byte, decoding produces exactly
one 20 ms frame. The host applies it to every frame it relays (part 2).

A decoder never throws on what the network sends. A packet that fails the check is concealed instead, and so is one
that Concentus rejects; the decoder then resets, because a throw can leave it half updated. Fuzzing with 20,000 random
payloads behind valid TOC bytes produced no rejection once the defect above was fixed.

### 1.5 Allocation and cost

The wrapper writes into the caller's buffers and allocates nothing: a test runs the same frames through the bare
Concentus objects and requires the same allocation, byte for byte, and the same output. Concentus itself allocates
scratch arrays for every frame, as upstream does: about 104 KB per encode and 12 KB per decode in wideband. A talker
and three listeners therefore make about 7 MB of short-lived garbage a second. The voice worker thread, which the next
slice adds, keeps that and the coding time off the game's main thread; removing it would mean rewriting upstream.

The Mono spike measured complexity 10 at about 0.43 ms per encode and 0.09 ms per decode. A test runs a four-player
session's codec work for one second of speech, four encodes and twelve decodes per frame, on one thread, and requires
it to finish within real time; it takes about 0.1 s on the development machine once compiled, 0.3 s cold.

### 1.6 Determinism

With analysis off and `PARITY` on, encoding and decoding are integer arithmetic throughout. A test freezes the SHA-256
of the packets for five seconds of synthetic speech at the default settings and at complexity 5, 20 kbit/s and a 20%
hint, and of the decoded audio. The test signal uses only IEEE basic arithmetic, never the runtime's sine, whose last
bit differs between C libraries. The hashes match on Windows and Linux under .NET 10; Mono was not run.

### 1.7 Tests

`OpusVoiceCodecTests` and `OpusVoiceCostTests`, in the multiplayer test project:

- round-trip SNR on synthetic voiced speech: at least 7 dB at complexity 10 and 5 (measured 9.0 and 9.1 dB), since a
  perceptual codec does not match the waveform closely, while a lost or misaligned signal scores near 0 dB;
- packet bounds at both bitrates and on full-scale noise, with every packet SILK-only wideband;
- loss: with a lost packet every 20 frames, FEC recovery beats concealment by at least 2 dB and on at least half the
  lost frames, and without a loss hint it does not;
- DTX: sustained silence becomes TOC-only packets that decode to near-silent comfort noise, and speech resumes at once;
- the frozen hashes;
- TOC validation over all 256 values and the length bounds;
- the four-player time budget, the allocation comparison, 4,000 hostile packets, setting bounds, resets, and the Speex
  resampler turning 44.1 and 16 kHz tones into 48 kHz at the same pitch and level.

## Part 2: the engine's voice path

The code lives in `src/TopiaForge.Multiplayer.Engine/Voice/` and `Core/SessionEngine.Voice.cs`, and every type is
internal: there is no mod-facing voice API. Voice is off unless `EngineOptions.Voice` names a codec, so the rig and the
solo provider run as before; an engine without voice options sends nothing for voice and ignores voice it receives.
The engine moves packets and never decodes them; the jitter buffer, which does, is a separate class that the provider's
voice worker will run, one per speaker.

### 2.1 The codec seam

`IVoiceCodec` describes a codec to the engine: its sample rate, samples per frame and largest packet, a header check
(`IsValidPacket`), and whether a packet is the codec's silence frame (`IsDiscontinuous`). It creates one
`IVoiceEncoder` per talker and one `IVoiceDecoder` per speaker. A decoder decodes, conceals a missing frame, rebuilds
one from the next packet's forward error correction, and resets. The engine itself calls only the header check.

The Opus wrapper of part 1 offers the same operations, and the next slice makes it implement the seam. The tests use `FakeVoiceCodec`, whose output
names the path that produced it: the frame's id when decoded, the previous frame's id when recovered, a marker when
concealed.

### 2.2 Frames and datagrams

A frame is one 20 ms codec packet with three more fields:

- **Sequence.** The sending engine numbers frames 1, 2, 3 and on, one per frame.
- **Timestamp.** The talker gives the frame's place in its stream, in frames. A gap means the talker sent nothing on
  purpose.
- **Flags.** `Discontinuous` marks the codec's silence frame, and `EndOfTalk` the last frame before the talker stops.

Frames travel in datagrams on the link's Voice channel, which is unreliable and sequenced: a lost datagram stays lost,
one older than the newest delivered is dropped, and nothing waits behind session frames.

| Field | Uplink, client to host | Relayed, host to clients |
| --- | --- | --- |
| Kind | `0x01` | `0x02` |
| Speaker | Absent | Varint: the host's index for the speaker |
| Stream | Absent | Varint: the generation of the speaker's stream |
| Sequence, timestamp | Varints, of the first frame | The same |
| Count | 1 or 2 | The same |
| Each frame | Flags byte, length varint, payload | The same |

A second frame's sequence and timestamp follow the first frame's by one. A reader refuses the whole datagram for an
unknown kind, a zero speaker index, a zero sequence, a sequence or timestamp at the 64-bit maximum, unknown flags, an
empty frame, a non-minimal varint or trailing bytes. It never throws. A one-frame relayed datagram of small numbers is
`02 09 04 01 00 01 00 03 01 02 03`: speaker 9, stream 4, sequence 1, timestamp 0, one frame with no flags and three
bytes of payload, so eight bytes of header; `VoicePacketTests` holds it as a vector.

**Stream generations.** The host gives a speaker's stream a new generation every time it starts: on every join and
rejoin, and once for the host's own player. One counter serves every speaker, so a generation never names two streams
in one session. A listener whose frames change generation knows the talker started again, even when the new stream's
first datagram never arrived; sequence numbers alone cannot show that, because a restarted talker numbers from 1 again,
and the old stream may have ended at 2. Generations from different sessions are unrelated.

**Two frames a datagram.** The plan caps a speaker at 30 packets a second, and 20 ms frames come 50 a second, so a
datagram carries two frames: 25 a second. Pairing also halves the secure transport's cost per datagram, about 40 bytes
(the protected packet's 17-byte header and 16-byte tag, and the DATAGRAM frame's type, channel, sequence and length).
At one frame a datagram, that overhead alone would take 16 kbit/s of a talker's 32 kbit/s class.

The price is one frame of extra delay for the first frame of each pair. `SubmitVoiceFrame` holds a frame until the
next one arrives, then sends both:

- a frame that does not follow on sends the held one alone;
- a held frame goes alone after two frames' time, because frames reach the engine up to a main-thread pump late and a
  single frame's time would split pairs;
- an end-of-talk frame never waits.

### 2.3 Speakers and the roster

The host gives every participant a speaker index when it admits them, its own player included. It never reuses an
index, so a late datagram can never change owner, and it stamps the index on everything it relays. The uplink format
has no speaker field, so a client cannot name another speaker. A client that sends the relayed format is refused as
malformed.

The roster (`VoiceRoster`, `0x30`, reliable, host to clients) maps each index to a participant id and says whether the
host muted them. The host sends it at most once per flush, when it changed: after a join or rejoin, when a
participant's record goes, after a server mute, and when an automatic mute starts or ends. A client keeps relayed
frames only from speakers in its roster, and never frames carrying its own index (`UnknownSpeaker`).

A rejoin within the participant record's 30-second grace keeps the speaker; it resets only the stream position, so
the new connection's sequences are accepted, and gives the stream a new generation. Moderation outlives the record: the
server mute, an automatic mute, and the strike and mute history live in a table keyed by the player's authenticated
identity for the whole session, and a player admitted again later gets them back with a new speaker index. The table
holds 1,024 players by default (`VoiceOptions.ModerationCapacity`); when it is full, the absent player seen longest ago
is forgotten first, preferring one with nothing on record, and players still present are never forgotten. A player is
seen when admitted, when they leave, and when their record expires, so one who stayed long and left last is not
mistaken for the oldest; an earlier version noted only admissions, and a player muted by hand who joined first was
forgotten before one who had left 91 seconds earlier. Leaving the session for another role clears the table.

### 2.4 The relay policy

The host takes voice from a client as soon as it has admitted the join, before the client acknowledges the session
snapshot as well as after. Links hand datagrams over before session frames, so a talker that starts the moment it is
ready sends its first datagram just behind its acknowledgement, and the host often reads the datagram first; an earlier
version waited for the acknowledgement and dropped it (`NotReady`). A datagram on a link the host has not admitted is
dropped.

`VoiceRelayPolicy` judges each client datagram, in this order:

| Check | Limit | Outcome |
| --- | --- | --- |
| Muted, by hand or automatically | None | Dropped on arrival, with no strike and no rate spent |
| Datagram rate | 30 a second; the token bucket holds one second | `RateLimited`; a strike only past twice the limit, sustained |
| Bitrate, counting whole datagrams | 40 kbit/s; the token bucket holds one second | `RateLimited`; a strike only past twice the limit, sustained |
| Format | Section 2.2 | `Malformed`, strike |
| Frame size | 160 bytes | `Oversized`, strike |
| Codec header | `IVoiceCodec.IsValidPacket`: for Opus, the TOC check of section 1.4 | `InvalidPacket`, strike |
| Order | The first sequence is not after the last one relayed | `Stale`, no strike, since networks reorder |
| Time | The first timestamp is not after the last one relayed, though the sequence is | `Malformed`, strike |

A datagram that passes is stamped with the speaker index and stream generation, relayed to every other ready
connection, except listeners who muted the speaker, and handed to the host's own player.

**Rate is judged kindly.** The host reads datagrams when its main thread pumps, so a stall on the host delivers a
backlog all at once. The buckets absorb a second of it and drop the rest without a strike, and fresh speech passes again
as they refill; a dropped datagram spends neither bucket. Only sustained excess is abuse. Each limit has a second bucket
that refills at twice the limit and holds five seconds of that: 300 datagrams refilled at 60 a second, and 50,000 bytes
refilled at 10,000 a second. Every datagram draws on both, relayed or not, and a datagram over a limit strikes only
when that limit's own sustained bucket is empty too. So:

- a flood of r datagrams a second is first struck after 300 / (r - 60) seconds, and a flood of b bytes a second after
  50,000 / (b - 10,000) seconds;
- from then on, datagrams past twice the limit strike, and twenty strikes within 10 seconds mute;
- a flood at or under twice a limit is dropped down to the limit and never struck, and one only just past twice the
  limit strikes too seldom to be muted. Either way the host relays no more than the limit.

Measured over the policy, with datagrams arriving evenly:

| Flood | First strike | Muted |
| --- | --- | --- |
| Small datagrams, 60 a second, twice the rate | Never | Never |
| Small datagrams, 61 a second | After 299 s | Never: one strike a second |
| Small datagrams, 90 a second | After 10.0 s | After 10.6 s |
| Small datagrams, 120 a second | After 5.0 s | After 5.3 s |
| Small datagrams, 240 a second | After 1.7 s | After 1.8 s |
| 332-byte datagrams, 25 a second (66 kbit/s) | Never | Never |
| 332-byte datagrams, 50 a second (133 kbit/s) | After 7.6 s | After 9.5 s |
| 332-byte datagrams, 59 a second (157 kbit/s) | After 5.2 s | After 6.6 s |
| 332-byte datagrams, 60 a second (159 kbit/s), over the rig | After 5.1 s | After 6.4 s |

An honest talker cannot empty either sustained bucket. It sends 25 datagrams a second against the 60 a second that
refill it, and a backlog is at most the 128 datagrams a link holds: under the 300-datagram allowance, and, even at the
largest datagrams the format allows, under the 50,000-byte one. Tests hold a talker at about 34 kbit/s on the wire
through host stalls of 0.5, 1.2, 3 and 6 seconds, and through stalls two seconds apart, without a strike.

Two earlier designs were wrong. One struck every datagram over a sliding one-second window, which muted honest talkers
after a 1.2-second host stall and removed them after three. The next counted only datagrams towards a strike, so a
flood of large datagrams kept under twice the datagram rate was never struck at any bitrate: 332-byte datagrams at 59 a
second, 157 kbit/s, earned no strike in two minutes.

**Escalation.** Twenty strikes within 10 seconds mute the speaker automatically for 30 seconds, and the host reports
`EngineDiagnosticKind.VoiceAbuse`, naming the participant and nothing else. The third automatic mute within 10 minutes
removes the speaker from the session (`Kicked`). `SetServerMute` mutes by hand until the host unmutes; unmuting also
ends an automatic mute, and the roster tells everyone.

The plan does not set these thresholds. They are chosen so that a sustained flood is muted within seconds, while a bad
link's occasional damaged frame never is. Honest traffic earns no strikes: it sends 25 datagrams a second against 30, and
about 26 kbit/s at 24 kbit/s of codec data against 40, with a second of burst for VBR peaks. A client also holds itself
to the same limits, so when frames bunch up after a stall it drops what would break them rather than sending it.
`VoiceOptions` holds every value.

### 2.5 Listener mutes

`SetListenerMutes(participants, muteAll)` records whom the local player does not want to hear. A client sends the set
reliably (`VoiceMutes`, `0x31`) when it changes and after every join, so the host stops relaying those speakers to it.
A host applies its own player's set locally. The set holds at most 256 ids.

Participant ids name players within one session only, so the set stays with its session: a reconnect to the same
session sends it again, and both leaving the session for another role and a reconnect that finds another session, as
after the host restarted, clear it, mute-all included. Muting `p2` on one host never silences a different `p2` on the
next, and never tells the next host whom the player had muted. An earlier version cleared the set only on a role
change, so a reconnect to a restarted host sent it the old set. A player's lasting wish, such as muting everyone, is
the voice service's to apply again in the new session.

### 2.6 The engine API

Every member runs on the owner thread, like the rest of the engine. The next slice's voice worker encodes and decodes
on its own thread and trades frames with the main thread through queues.

| Member | What it does |
| --- | --- |
| `SubmitVoiceFrame(timestamp, flags, payload)` | Sends or holds one encoded frame of the local talker. It refuses invalid packets, unknown flags and timestamps that do not move forward; says when voice is off, the session is not ready or the host muted the player; and drops frames that would break the host's limits (`RateLimited`). |
| `TryReceiveVoiceFrame(out frame)` | Dequeues the oldest received frame: speaker, stream generation, sequence, timestamp, flags, payload and arrival time. |
| `SetServerMute(participant, muted)` | Mutes or unmutes a participant for everyone. Host only. |
| `SetListenerMutes(participants, muteAll)` | Section 2.5. |
| `IsServerMuted(participant)` | Answers from the host's records, or from a client's roster. |
| `GetVoiceStatistics()` | Counts frames submitted, datagrams sent and relayed, frames delivered, drops by reason, strikes, automatic mutes and removals. |

Received frames wait in a bounded queue, 256 by default. When it is full the oldest frame goes (`InboxOverflow`),
since it is too late to play. A frame's payload points into the datagram the link handed over, which nothing reuses. A
headless host delivers nothing.

Losing the link clears the roster, the queue and any held frame. Leaving a session for another role clears them too,
with the listener mutes and, on a host, the moderation table, and lets the talker's timestamps start again.

### 2.7 The jitter buffer

`VoiceJitterBuffer` turns one speaker's frames, which arrive early, late or not at all, into exactly one frame per
`Pull`; the playback clock pulls every 20 ms. A buffer belongs to one thread and allocates nothing once running.

- **Target delay.** A frame's transit is its arrival time minus its place in the stream. Over the last 128 frames, the
  spread between the fastest transit and the 95th percentile is the jitter, and the target is that plus one frame,
  clamped to 40 to 200 ms. Clock offsets cancel out. Until eight frames have arrived the target is 60 ms, the floor plus
  a frame, because with frames in pairs the 40 ms floor would start a speaker's first talk spurt with no room at all.
- **Talk spurts.** A spurt starts when a frame arrives while the buffer is idle. It fills to the target, then plays one
  frame per pull. End of talk ends it, and so does a pause of 200 ms after a silence frame, so the next spurt fills to
  the target of the moment.
- **Loss.** A missing frame is rebuilt from the next frame's FEC when that frame is already here, and concealed
  otherwise. After three concealed frames, two more fade to silence and the spurt ends.
- **Intentional gaps.** A gap plays as silence, not concealment, when the sequences on either side of it are
  consecutive or a silence frame came before it.
- **Order.** The voice channel is sequenced, so one speaker's frames arrive in order, each at most once. A frame that
  arrives after its turn is late and dropped. A frame of another stream generation than the one before it, or numbered
  at or below one already received, comes from a new stream, after a rejoin or a restart, and resets the buffer, its
  estimates and the decoder, however short the old stream was. The generation catches what numbers cannot: a restart
  whose first datagram was lost resumes at sequence 3 after an old stream that ended at 2. Without it, the old stream's
  timing stayed in the estimate, and the target rose to the 200 ms ceiling instead of staying near 50 ms. An earlier
  rule waited for numbers 32 behind, so a restart after a stream of under 33 frames lost its first frames as late.
- **Drift.** A frame's slack is how many frames ahead of its turn it arrives. The 5th percentile of the last 64 slacks
  steers drift control. At zero a concealed frame is inserted, so playout waits longer; at three a frame is dropped,
  decoded and discarded so the codec state stays continuous. A correction waits for 32 fresh arrivals and half a
  second, never takes the delay outside 40 to 200 ms, and nothing is inserted while an end of talk is already buffered.

A percentile holds still under jitter, where the fill level does not, so corrections follow the clocks rather than the
noise. A first design that steered by the fill level oscillated at high jitter.

### 2.8 The rig

`VirtualEngineTransport` now models both of the real transport's channel contracts (`MultiplayerSessionEngine.md`,
"The rig on the engine"). Realtime and Voice are unreliable and sequenced, and a path that is down loses their
datagrams rather than holding them. Datagrams serialize in a lane of their own, as the real transport writes queued
datagrams first in every packet, but their bytes still count against the link, so later session frames wait for them.
Both lanes keep their time in millionths of a tick, and only the tick a frame is delivered in is rounded up, so a small
datagram costs its bytes, never a whole tick; a frame arrives in the tick after its last byte, so each hop adds at most
a tick to a link with no rate at all. An earlier version rounded every frame up to a tick: with any rate set, neither
lane carried more than 60 frames a second, and three talkers relayed to one listener over links a million bytes a
second, under 1% used, fell 1.4 s behind in five seconds, with the session stream 76 ticks behind them. A link's reader
gets waiting datagrams before session frames, and a link holds at most 128 datagrams for it, dropping the oldest, as
the real connection does. The transport counts datagrams lost, dropped as stale and overflowed, and bytes and frames
per channel. Every existing rig test passes unchanged, since none sent datagrams before.

### 2.9 Tests

These run in the multiplayer test project, and every one is deterministic:

- `VoicePacketTests`: one- and two-frame round trips with the relayed vector of section 2.2 and the widest datagram,
  every malformed datagram refused whole, and the roster and mute messages;
- `VoiceJitterBufferTests`: the target against jitter and its clamps, a second spurt playing cleanly once the target has
  adapted, FEC then concealment then fade, 1% clock drift each way corrected without concealment, late frames and old
  numbers starting a new stream, pauses and talk spurts, restarts by number and by generation, and no allocation in
  steady playback;
- `VoiceRelayPolicyTests`: each limit and check, a backlog absorbed while only sustained excess strikes, the first
  strike and the mute of datagram and bitrate floods at the times in section 2.4, and floods at or under twice a limit
  never struck, honest talkers through host stalls of up to 6 s, stale and backwards streams, escalation to a mute and
  to removal, server mutes, and part 1's TOC check run through the seam;
- `VoiceSessionTests`, over the rig: speakers stamped by the host, a forged speaker refused, a client refusing frames
  its roster does not explain, a flood muted then removed, server and listener mutes across a reconnect, an engine
  without voice options staying silent, a restarted client heard from its first frame, and voice across role changes;
- `VoiceBudgetTests`: four players talking at once for five seconds; voice beside 8 KiB session messages with a quarter
  of all frames lost, arriving within 70 ms while the session stream waits for retries; three talkers on links a
  million bytes a second, within a tick a hop of links with no rate; pairing; a stalled talker paced rather than
  struck; and the bounded queue;
- `VoiceModerationTests`, over the rig: host stalls of 0.5, 1.2, 3 and 6 s and repeated ones striking nobody, a bitrate
  flood struck and muted, a server mute and the ladder to removal outliving the grace period, the moderation table's
  bound and what it forgets first, listener mutes staying in their session after a role change or a reconnect to
  another session, and a short stream that restarts playing from its first frame;
- `VoiceRestartTests`, over the rig: a restarted talker heard from the datagram that travels with its acknowledgement,
  and a restart noticed by host and client alike when the path loses its first datagram;
- `VirtualVoiceChannelTests`: the rig's datagram channels, their own lane on a busy link, link time kept in fractions
  of a tick, and the link's datagram-first, bounded reads.

### 2.10 The budget

The four-player test measures what voice costs, counting 40 bytes of transport overhead per datagram. Relayed
datagrams carry the stream generation, one byte more than the uplink's:

| Codec data | One talker's uplink | Three speakers relayed to one client |
| --- | --- | --- |
| 20 kbit/s, 50-byte frames (measured) | 29.8 kbit/s, of a 32 kbit/s class | 90.6 kbit/s, of a 96 kbit/s class |
| 24 kbit/s, 60-byte frames (measured) | 33.8 kbit/s | 102.6 kbit/s |

So at part 1's default of 24 kbit/s, voice exceeds both of the transport's voice classes, which would shed it at every
peak. Before voice ships, either the encoder runs at 20 kbit/s or the classes grow to fit 24 kbit/s.
