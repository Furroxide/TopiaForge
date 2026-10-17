# Multiplayer voice — design record

Status: Implementing (WS2g of the live-multiplayer plan) · Date: 2026-10-13 · Owner: multiplayer

This records voice chat below the user interface: the codec the provider runs, the session engine's path for voice
frames between peers, and the provider's voice service around them. Microphone capture in Unity, emitters and every
in-game control come in later workstreams. No player-facing behaviour changes until then.

WS2g lands in three pull requests, each with its own part of this record:

1. **The codec** (part 1): Opus through vendored Concentus, wrapped for the provider.
2. **The engine's voice path** (part 2): the codec seam, the frame format, the jitter buffer and the host's relay
   policy.
3. **The voice service** (part 3): capture, transmit modes, the voice worker, playback streams and the provider's
   wiring.

## Part 1: the codec

The code lives in `mods/TopiaForge.Multiplayer/Voice/`, inside the provider only, and every type is internal. The
engine never names a codec; the provider hands it one through the seam in part 2.

### 1.1 Settings

| Setting | Value |
| --- | --- |
| Format | 48 kHz mono, 16-bit samples |
| Frame | 20 ms, 960 samples, one Opus frame per packet |
| Application | VOIP, signal type voice |
| Bitrate | Unconstrained VBR with a 20 kbit/s target, which keeps a talker within the transport's voice share (section 2.10) |
| Audio band | Capped at wideband (8 kHz), so the encoder stays in SILK-only mode |
| Silence | Discontinuous transmission: TOC-only packets after a hangover |
| Loss | In-band FEC, sized by an expected-loss hint (10% by default, 0 turns it off) |
| Complexity | 10 by default, 5 to 10 allowed |
| Packet | At most 160 bytes |

`OpusVoiceCodec` holds the settings, validates packets, and creates one `OpusVoiceEncoder` per talker and one
`OpusVoiceDecoder` per speaker. Encoders and decoders belong to one thread each. Complexity and the loss hint can
change live on an encoder; the voice service feeds the hint from the loss its transport measures (section 3.5).

The bitrate was 24 kbit/s by default at first, with 20 to 24 kbit/s allowed. Section 2.10 found that 24 kbit/s does not
fit the transport's voice shares, so the encoder now targets 20 kbit/s. It is a target, not a cap: VBR follows the
input. Ten seconds of the test speech code at 19.7 kbit/s, and full-scale white noise, the hardest input there is, at
23.9 kbit/s, which still fits the client's share.

### 1.2 Why wideband

At 20 to 24 kbit/s the encoder picks fullband hybrid mode (TOC configuration 15) unless capped. Hybrid splits the
bits between SILK and CELT, and SILK's share stays under the threshold at which it codes in-band FEC (LBRR) at all.
Measured on the synthetic speech the tests use, 24 kbit/s, complexity 10, on .NET 10, before the 20 kbit/s target; at
20 kbit/s the loss test of section 1.7 still finds FEC well ahead of concealment:

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
and three listeners therefore make about 7 MB of short-lived garbage a second. The voice worker thread of part 3 keeps
that and the coding time off the game's main thread; removing it would mean rewriting upstream.

The Mono spike measured complexity 10 at about 0.43 ms per encode and 0.09 ms per decode. A test runs a four-player
session's codec work for one second of speech, four encodes and twelve decodes per frame, on one thread, and requires
it to finish within real time; it takes about 0.1 s on the development machine once compiled, 0.3 s cold.

### 1.6 Determinism

With analysis off and `PARITY` on, encoding and decoding are integer arithmetic throughout. A test freezes the SHA-256
of the packets for five seconds of synthetic speech at the default settings and at complexity 5, 20 kbit/s and a 20%
hint, and of the decoded audio. The test signal uses only IEEE basic arithmetic, never the runtime's sine, whose last
bit differs between C libraries. The hashes match on Windows and Linux under .NET 10; Mono was not run. The default
ones moved once, with the 20 kbit/s target, and were checked on Linux again.

### 1.7 Tests

`OpusVoiceCodecTests` and `OpusVoiceCostTests`, in the multiplayer test project:

- round-trip SNR on synthetic voiced speech: at least 7 dB at complexity 10 and 5 (measured 8.4 and 8.2 dB at 20
  kbit/s, 9.0 and 9.1 dB at 24 kbit/s), since a perceptual codec does not match the waveform closely, while a lost or
  misaligned signal scores near 0 dB;
- packet bounds at the bitrate and on full-scale noise, with every packet SILK-only wideband;
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
voice worker runs, one per speaker (part 3).

### 2.1 The codec seam

`IVoiceCodec` describes a codec to the engine: its sample rate, samples per frame and largest packet, a header check
(`IsValidPacket`), and whether a packet is the codec's silence frame (`IsDiscontinuous`). It creates one
`IVoiceEncoder` per talker and one `IVoiceDecoder` per speaker. A decoder decodes, conceals a missing frame, rebuilds
one from the next packet's forward error correction, and resets. The engine itself calls only the header check.

The Opus wrapper of part 1 implements the seam (part 3). The tests use `FakeVoiceCodec`, whose output names the path
that produced it: the frame's id when decoded, the previous frame's id when recovered, a marker when concealed.

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
when that limit's own sustained bucket is empty too:

- a flood is first struck once the sustained bucket of the limit that drops its datagrams is spent: after
  300 / (r - 60) seconds for r datagrams a second that the datagram rate drops, and after 50,000 / (b - 10,000) seconds
  for b bytes a second that the bitrate drops. The byte formula holds only when the bitrate is what drops them. When the
  datagram rate drops enough of a flood first, the bitrate's one-second bucket never empties, and the flood never
  strikes for bitrate at all;
- from then on, a datagram over a limit strikes whenever that limit's sustained bucket is empty, and twenty strikes
  within 10 seconds mute.

So floods well past twice a limit are struck, muted and then removed. Floods only just past twice a limit are
rate-limited and never muted: they strike too seldom, or not at all. A flood at or under twice a limit is dropped down
to the limit and never struck. Either way the host relays no more than the limits, so a flood that is never muted gets
no more relayed than the limits allow any talker.

Measured over the policy, with datagrams arriving evenly. The four 334-byte rows run for ten minutes, with sequences of
three bytes:

| Flood | First strike | Muted |
| --- | --- | --- |
| Small datagrams, 60 a second, twice the rate | Never | Never |
| Small datagrams, 61 a second | After 299 s | Never: one strike a second |
| Small datagrams, 90 a second | After 10.0 s | After 10.6 s |
| Small datagrams, 120 a second | After 5.0 s | After 5.3 s |
| Small datagrams, 240 a second | After 1.7 s | After 1.8 s |
| 332-byte datagrams, 25 a second (66 kbit/s) | Never | Never |
| 334-byte datagrams, 31 a second (82 kbit/s) | After 140 s | Never: at most 7 strikes in any 10 s |
| 334-byte datagrams, 33 a second (88 kbit/s) | After 49 s | Never: at most 18 strikes in any 10 s |
| 334-byte datagrams, 34 a second (90 kbit/s) | After 37 s | After 47 s |
| 334- and 30-byte datagrams in turn, 60 a second (87 kbit/s) | Never: the datagram rate drops half, and the rest stay within the bitrate | Never |
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
about 22 kbit/s at the 20 kbit/s target against 40, with a second of burst for VBR peaks. A client also holds itself
to the same limits, so when frames bunch up after a stall it drops what would break them rather than sending it.
`VoiceOptions` holds every value.

### 2.5 Listener mutes

`SetListenerMutes(participants, muteAll)` records whom the local player does not want to hear. A client sends the set
reliably (`VoiceMutes`, `0x31`) when it changes and after every join, so the host stops relaying those speakers to it.
A host applies its own player's set locally. The set holds at most 256 ids, and `GetListenerMutes` reads it back.

Mutes follow the people. A participant id names a player only while the host that assigned it keeps that player's
record, so the set lasts as long as the client knows the host kept the record of the player who made it:

- **Kept.** An in-place replacement (`ReplaceSession`) keeps every record, and a reconnect within the 30-second grace
  finds the record it left. The ids still name the same people, so the set stays, whether the client stayed connected
  through the replacement or reconnected into the new session afterwards.
- **Forgotten.** A join that finds a host which did not keep the record, because it is another host, restarted, or let
  the record expire, forgets the set, mute-all included. `JoinAccepted` says whether the host kept the record. The set
  also remembers the host it was made with, by the host's authenticated identity, so another host's kept record does
  not count: a traveller who returns within the grace to a host it visited, after muting players elsewhere, leaves
  those mutes behind.
- **An unanswered join counts as a lost record.** When the link goes after a join's request left but before its answer
  came, the host may have made the record meanwhile, also as a new process after a restart. The next join would then
  find a record kept that the client never saw made. So losing the link forgets the set made with that host, and the
  ids of that host's list name nobody until it sends its list again. A join replaced before its request left changes
  nothing.
- **Names belong to the host that listed them.** A player can name only the players of the last list a host sent the
  client. The new host sends its own after the snapshot, which for a large world can come a second or more after
  `JoinAccepted`, and until then a join that found another host, or this one without the player's record, withholds the
  old list, so the session lists nobody (`MultiplayerSessionEngine.md`, "Participants and connections"). So names given
  while no session is joined, or while a join is under way, belong to the host that sent the last list, and the join
  keeps or forgets them as above. Names given after a join has found another host, or this one without the player's
  record, but before that host lists its players, name nobody in the session and go at once. Names given before any
  list, or after a role change, belong to no host, and every join forgets them. A set that names nobody, such as muting
  everyone, applies to the session the join finds, whichever that is, whether it was made with no link or during the
  join.
- **Role changes** forget the set.

So muting `p2` on one host never silences a different `p2` on the next, and never tells the next host whom the player
had muted. Two earlier versions went by the session. The first cleared the set only on a role change, so a reconnect to
a restarted host sent it the old set. The second forgot the set whenever a join named another session id: a client
whose link was down while the host replaced its session in place came back without its mutes, though one that stayed
connected kept them, and a set made between moving the link and the `JoinAccepted` was dropped when the join found
another session, though the same call after a role change was kept. A third applied every set made while a join was
under way to the session the join found, names included, so a name given from the old host's list silenced whoever had
that id on the new one, and a caller had to hold names back by session, which lost them across a reconnect into an
in-place replacement. A fourth tied names given while no session was joined, or while a join was under way, to the host
joined last. A client then went on listing the previous host's players until the new host's list arrived, so a name
given from that list after the new host's `JoinAccepted`, or while the link was down before its list came, still
reached the new host. A fifth trusted every `JoinAccepted` that said the record was kept. A restarted host's first
answer to a join can be lost with the link after the host has made the record, so the next join found it kept, and both
the set and the names given from the old process's list reached the new process.

### 2.6 The engine API

Every member runs on the owner thread, like the rest of the engine. The voice worker of part 3 encodes and decodes on
its own thread and trades frames with the main thread through queues.

| Member | What it does |
| --- | --- |
| `SubmitVoiceFrame(timestamp, flags, payload)` | Sends or holds one encoded frame of the local talker. It refuses invalid packets, unknown flags and timestamps that do not move forward; says when voice is off, the session is not ready or the host muted the player; and drops frames that would break the host's limits (`RateLimited`). |
| `TryReceiveVoiceFrame(out frame)` | Dequeues the oldest received frame: speaker, stream generation, sequence, timestamp, flags, payload and arrival time. |
| `SetServerMute(participant, muted)` | Mutes or unmutes a participant for everyone. Host only. |
| `SetListenerMutes(participants, muteAll)` | Section 2.5. |
| `GetListenerMutes(into)` | Copies the listener mute set, as section 2.5 kept or forgot it, and returns mute-all. A voice service reads it rather than keeping a copy that could disagree. |
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
the real connection does. The transport counts datagrams lost, dropped as stale, overflowed and shed, and bytes and
frames per channel. Every existing rig test passes unchanged, since none sent datagrams before.

**Congestion.** A datagram the link cannot carry in time is shed, never queued, and counted (`DatagramsShed`):

- one whose lane is already more than three ticks behind now, 50 ms at the rig's 60 ticks a second
  (`VirtualEngineTransport.DatagramBacklogTicks`);
- while the session lane is more than three ticks behind, one past its channel's share of the link: a bucket three ticks
  deep, refilled at the real transport's class caps, 96 of every 256 bytes for Voice and 64 for Realtime, so session
  keeps the rest.

The real transport behaves the same way: a connection drops the datagrams its queue cannot hold, and the class caps
leave the session class at least 96 of each connection's 256 kbit/s, so voice is what goes first. Three ticks is longer
than the 40 ms between one talker's datagrams, so a link fast enough for its talkers clears a datagram from each before
the next arrives, and voice within the rate is never shed. A shed datagram never reaches the wire: it costs the link
nothing, takes no sequence, and counts as neither sent nor lost. The rig's bound is shorter than the real 64-datagram
queue, which at the voice class rates holds half a second to two seconds of voice; section 2.11 has the consequence.

An earlier version never lost a datagram to congestion. Three talkers of 80-byte frames sent one listener about
12,800 bytes a second over links of 8,000: every frame arrived, the worst 6.2 s late, and with a 100-byte session
message ten times a second beside them the session lane fell 442 ticks behind in ten seconds. Now 342 of the 750
relayed datagrams are shed, the worst frame arrives 183 ms after it was spoken, against 133 ms for one talker on the same
link, and no session message waits more than 8 ticks. The bound alone would not have done it: without the share, voice
still filled the link and the session lane fell 79 ticks behind.

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
  never struck, floods just past twice the bitrate struck when the byte formula says but never muted, with the edge at
  34 datagrams a second, and a flood the datagram rate cuts down never struck for bitrate, honest talkers through host
  stalls of up to 6 s, stale and backwards streams, escalation to a mute and to removal, server mutes, and part 1's TOC
  check run through the seam;
- `VoiceSessionTests`, over the rig: speakers stamped by the host, a forged speaker refused, a client refusing frames
  its roster does not explain, a flood muted then removed, server and listener mutes across a reconnect, an engine
  without voice options staying silent, a restarted client heard from its first frame, and voice across role changes;
- `VoiceBudgetTests`: four players talking at once for five seconds; voice beside 8 KiB session messages with a quarter
  of all frames lost, arriving within 70 ms while the session stream waits for retries; three talkers on links a
  million bytes a second, within a tick a hop of links with no rate; pairing; a stalled talker paced rather than
  struck; the bounded queue; three talkers over links of 8,000 bytes a second shed while session messages keep their
  pace; and voice within the rate, or within its share beside large session messages, never shed;
- `VoiceModerationTests`, over the rig: host stalls of 0.5, 1.2, 3 and 6 s and repeated ones striking nobody, a bitrate
  flood struck and muted, a server mute and the ladder to removal outliving the grace period, the moderation table's
  bound and what it forgets first, listener mutes staying in their session after a role change or a reconnect to
  another session, and a short stream that restarts playing from its first frame;
- `VoiceRestartTests`, over the rig: a restarted talker heard from the datagram that travels with its acknowledgement,
  and a restart noticed by host and client alike when the path loses its first datagram;
- `VoiceMuteTests`, over the rig: `JoinAccepted`'s vector, which says whether the host kept the record; mutes that
  survive an in-place replacement however the client resynchronizes; mutes forgotten when the record expired, and when
  a host other than the one they were made with kept it; a set that names nobody, made while joining or with no link,
  applied to the session the join finds, with or without a role change first; and, for a host whose snapshot spans two
  frames over a slow link, the session listing nobody in between, a name from the old host's list given after
  `JoinAccepted` dropped at once, one given while the link was down before the new list came left with the old host,
  and one from a restarted host's old list, whose number the new process has given to someone else, dropped too; and,
  when the new process's first answer to each traveller is lost with the link, a set made with the old process and a
  name given afterwards from its list both forgotten, while a join replaced before its request left keeps the set;
- `VirtualVoiceChannelTests`: the rig's datagram channels, their own lane on a busy link, link time kept in fractions
  of a tick, the link's datagram-first, bounded reads, and datagrams shed when their lane is more than three ticks
  behind, or past their share while the session lane is.

### 2.10 The budget

Two costs matter. On the wire, each datagram also carries about 40 bytes of the secure transport: the protected
packet's 17-byte header and 16-byte tag, and the DATAGRAM frame's type, channel, sequence and length. That is what the
connection's 256 kbit/s budget pays, and what the four-player test counts. The class shaper charges each datagram only
its DATAGRAM frame against the voice share: the payload and 4 to 7 bytes, the frame's type and channel and its
sequence and length as varints. The share is 4 KiB/s (32.8 kbit/s) from a client and 12 KiB/s (98.3 kbit/s) from the
host to each client. Relayed datagrams carry the stream generation, one byte more than the uplink's.

| Codec data | One talker's uplink, on the wire | Three speakers relayed to one client, on the wire |
| --- | --- | --- |
| 20 kbit/s, 50-byte frames (measured) | 29.8 kbit/s | 90.6 kbit/s |
| 24 kbit/s, 60-byte frames (measured) | 33.8 kbit/s | 102.6 kbit/s |
| The real codec at its 20 kbit/s target (measured, part 3) | 29.8 to 29.9 kbit/s | 90.6 to 90.7 kbit/s |

What the shaper charges, for the real codec: the test speech comes to 22.5 kbit/s a talker, 23.0 in its busiest
second, against the client's 32.8; full-scale white noise, the hardest input, to 26.8 kbit/s, 27.7 in its busiest
second. Both fit. So at part 1's first default of 24 kbit/s, voice exceeded the classes at every peak; the encoder now
targets 20 kbit/s instead of the classes growing.

**The downlink limit.** The host's 98.3 kbit/s share to each client fits about four talkers of the test speech at once,
and three of full-scale noise. Nothing selects among speakers beyond that: with more talking at once, the host's voice
queue to that client fills, and what it cannot carry is late or dropped (section 2.11). Choosing the loudest speakers is
later work.

### 2.11 Later work

This section first listed what the voice service had to do with the engine's path. Part 3 does it: the Opus types
implement the seam and the provider sets `EngineOptions.Voice` (3.7); received frames reach one `VoiceJitterBuffer` per
speaker on the voice worker with their arrival times and stream generations, and every jitter buffer goes when the
session id changes, because each host process numbers its streams from 1 again (3.4); silence and the end of talk are
flagged and stale capture is dropped (3.2, 3.3); the listener mutes are read back from the engine rather than copied,
and only the wish to hear nobody is given back where the engine forgets it (3.5); and the bitrate is settled (2.10).

The transport's voice queue holds up to 64 datagrams for the class share. Under a sustained overload that is half a
second to two seconds of voice at the class rates (12 KiB/s down, 4 KiB/s up, depending on the frame size), past the
jitter buffer's 200 ms ceiling, so the frames that do arrive would be too late to play. Shedding queued voice by age, as
the rig's bound does, would keep it fresh. The same overload comes from more than about four talkers at once (2.10),
and selecting the loudest of them is later work too.

## Part 3: the voice service

The provider's side of voice lives in `mods/TopiaForge.Multiplayer/Voice/`, and every type is internal. It covers
everything between the microphone's samples and each speaker's playback samples. The Unity pieces come later: capture
from the microphone, emitters, the push-to-talk binding, consent and the settings page (WS5), and the internal SPI that
ModManager calls (WS3). The public members of `VoicePlaybackStream` are those of that SPI's playback stream, so WS3
hands out the streams as they are. `VoiceService`'s members are close to its voice service, but the SPI as drafted has
no way to turn voice on or to say when the player transmits, so WS3 adds `Enabled` and `SetTransmitting` to it or
drives them itself. `OpusVoiceCodec`, `OpusVoiceEncoder` and `OpusVoiceDecoder` now implement the engine's seam from
part 2.

### 3.1 Threads

| Thread | Does | Never |
| --- | --- | --- |
| Main, the engine's owner | Every member of `VoiceService`; capture, transmission, `Enabled`, the mutes, `GetPlaybackStream`, `IsSpeaking` and `GetStatistics` refuse any other thread. `Pump` hands capture to the worker, gives the engine encoded frames, takes received frames from it and watches the session | Encodes, decodes or waits |
| Voice worker | Resampling, framing, voice activation, encoding, the jitter buffers, decoding, filling the playback streams | Touches the engine |
| Audio | `VoicePlaybackStream.Read` | Locks, waits or allocates |

The main thread and the worker trade work through two double-buffered exchanges (`VoiceExchange`). The producer adds to
a batch under a brief lock; the consumer swaps the whole batch out, works on it without the lock, and gives it back.
Both batches are allocated once, so steady traffic allocates nothing there. Control for the speakers keeps its place
among the received frames in a batch, so a reset drops only the frames queued before it, however far the worker has
fallen behind. The audio thread reads a single-producer, single-consumer ring.

`Pump` runs once per game frame, after the engine's own pump. The worker runs on a background thread at above-normal
priority. It wakes when the main thread queues work, or when its next 20 ms playout slot falls due, and sleeps when
there is nothing to do. Tests run its step on a virtual clock instead. A step that throws is counted, and ten in a row
stop the worker and voice with one `VoiceFailed` diagnostic, which the provider passes on as `engine.voice-failed`. No
exception leaves the thread. From then on nothing takes what the main thread queues for the worker, so each pump drops
it; otherwise every mute, stream and capture the session went on producing would pile up for as long as it lasted.

### 3.2 Capture

- **Voice stays off until the player turns it on.** Until `Enabled` is set, nothing is captured, coded or sent, and the
  meter reads silence; that holds in the service, not only in the interface. Turning it off stops at once: the worker
  codes nothing more, and frames already coded that have not left are dropped. Voice from other players still plays.
- `SubmitCapture(samples, count, sampleRate)` takes float samples at the device's rate, on the main thread, and refuses
  any other thread, so capture from an audio callback is handed to the main thread first.
- **What one call keeps follows the frame rate.** The service smooths the interval between capture calls and between
  pumps. One call keeps its newest 60 ms, or one and a half frame intervals when the main thread runs slower than 25
  frames a second; more than that arrived at once after a stall, and sending it would only add delay. An interval over
  250 ms is a stall or a pause, not a frame rate, and leaves the estimate as it was, so a stall's backlog is still cut.
  An encoded frame may wait 120 ms, or what one call keeps and one and a half pumps more, before it is dropped as late.
  A fixed 60 ms cut every call below about 17 frames a second: at 70 ms frames 14% of capture was lost, at 100 ms 40%.
- The worker resamples to 48 kHz with the vendored Speex resampler (quality 5) and cuts 20 ms frames.
- Timestamps follow the clock. After a gap of more than 40 ms, such as a released key or a stall, the stream resumes at
  the frame the clock has reached, so listeners see a pause as a pause and their delay estimates hold.

### 3.3 Transmit modes

- **Transmitting is explicit.** `SetTransmitting(true)` while the push-to-talk key is held, or while voice activation
  listens; `SetTransmitting(false)` ends the spurt at once: the frame being filled is padded with silence and sent with
  `EndOfTalk`. Capture handed over while not transmitting is only metered. Each capture carries the state it was made
  in, so the end lands exactly between the last capture transmitted and the first that is not, whatever the cadence of
  the calls. An earlier version took a pump with no capture since the last one for a released key, so at an audio
  callback's cadence it ended a held key's spurt at every pump that happened to see no capture.
- **Push-to-talk**, the default, sends every frame captured while transmitting. When the key stays down while the
  microphone yields nothing for more than 40 ms, the end of talk comes at the frame the clock has reached, so it is
  neither stale nor late.
- **Voice activation**, while transmitting, treats a frame at or above the threshold (-40 dBFS by default) as speech.
  The spurt stays open for a 300 ms hangover after the last such frame, and the hangover's fifteenth frame carries
  `EndOfTalk`. Every frame is metered, for the settings page.
- **Silence.** The encoder's DTX packets are one byte. The first of a run goes out marked `Discontinuous`, so listeners
  play silence rather than conceal; the rest of the run is not sent. An end of talk is always sent.
- **Nobody to hear.** In a solo session, when muted by the host, or when not connected, frames are cut and metered but
  not coded. `TransmitState` says which. A host counts only players who are Ready as listeners, since it relays to no
  one else: a player still joining, perhaps held short of Ready while the runtime loads the world, hears nothing yet.

### 3.4 Receiving and playback

- The main thread drains `TryReceiveVoiceFrame` and copies each frame, with its engine-clock arrival time and stream
  generation, into the exchange. It creates a speaker's playback stream the first time that speaker is heard or
  `GetPlaybackStream` asks.
- **The session bounds the speakers.** Each costs a 64 KiB ring, a jitter buffer and a decoder, so there are at most
  `EngineOptions.MaximumParticipants` streams at once, as the engine had it when voice started: 16 by default, the most
  a live session allows. That holds whatever a host's roster names.
  - A speaker the session's participants list always gets one, ahead of the rest: when there is no room, the stream
    unlisted the longest ends.
  - One they do not list yet, as when voice comes just before the participant update that names its speaker, gets one
    only while there is room. It keeps it for 2 s unless the participants list them by then; after that the stream
    ends, and a later frame takes a new one only while there is room.
  - Past the bound, frames are dropped and counted, and `GetPlaybackStream` returns an ended stream that plays silence.
  - A host that names 2,000 strangers, a hundred to a roster, leaves a listener at most 16 streams, and none of them
    2 s later.
- On the worker, each speaker has a `VoiceJitterBuffer` with an Opus decoder. Every 20 ms slot pulls one frame and
  writes it to the speaker's `VoicePlaybackStream`.
- **Stalls leave no lasting delay.** A stall of this player's main thread, of the host's, or of the worker hands a
  speaker's backlog over at once.
  - A speaker's frames that come out of one pump spanning more than 200 ms, the most delay a jitter buffer aims for, get
    the arrival times they would have had in step with the newest of them. Stamped with the moment they were read, they
    would look like a second of jitter and push the delay target to its ceiling for the next 128 frames.
  - After any batch that leaves a jitter buffer more than 11 frames deep, the worker trims it: frames are decoded, so
    the codec state stays continuous, and dropped until the buffer holds its target and one frame. Left in place, the
    backlog played late until drift control caught up, a frame every half second or more.
  - A worker step more than five slots late follows a stall of its own: the missed slots are dropped rather than run in
    a burst, and every buffer is trimmed the same way.
  - With any of the three stalled for a second mid-speech, speech plays within the 150 ms bound again 0.6 s after the
    stall ends.
- **Two clocks.** The audio device's clock drifts from the worker's. The channel watches the stream's lowest level over
  each second of slots, measured just before it writes, which is the cushion left after the reader's reads.
  - Below 10 ms, it pulls one extra frame.
  - More than three frames above that, it skips one pull.
  - The jitter buffer's own drift control then sees a slightly faster or slower listener, which it already handles.
  - A reader that just arrived, or one that ran dry, gets a cushion for its largest read at once.
- A stream nobody has read for 200 ms, such as one whose avatar has no emitter yet or whose audio source is paused,
  still has its jitter buffer pulled on time, but the audio is dropped instead of filling the ring, and what the reader
  left in the ring when it stopped is flushed. A reader that comes back hears the speaker as they are now, never what
  they said before the pause.
- **The ring.**
  - `Read(buffer, offset, count)` is wait-free.
  - The ring holds 16,384 samples, about 341 ms.
  - An underrun yields silence and is counted.
  - A backlog over 200 ms is skipped down to the newest 60 ms.
  - Each side owns its own position, so a flush is a position the reader skips to.
  - A read loads the flush position before the end, and never skips past the end it loaded. The writer flushes only to
    an end it has published, so the end loaded second is never behind. Loaded the other way round, a write and a flush
    between the two loads put the flush past the end, and the read's copy got a negative length.
- **Resets.**
  - A speaker who rejoins or restarts sends a new stream, and the host stamps it with a new generation, so their
    jitter buffer and decoder start again at the first frame of it to arrive. The service resets nothing itself for a
    rejoin. That first frame can reach a listener before the session's participants say the speaker is back, because
    the host relays voice at once while the reliable stream may lag. An earlier version reset a speaker when the
    participants showed them back, and so dropped the new stream's first frames whenever that update came late.
  - When the session id changes, and when this player is Ready again after a lost or stalled link, every jitter buffer
    goes, after the frames already queued for the worker and before any that follow. Each host process numbers its
    streams from 1, so a buffer kept from another session could take a new stream for an old one; a host's own session
    never leaves Ready, so the new id alone decides.
  - A speaker who leaves gets an ended stream, which plays silence and counts nothing. A join that withholds the
    participant list lists nobody until the new host's list arrives (`MultiplayerSessionEngine.md`, "Participants and
    connections"), so every speaker of the session before leaves then, and the new host's players get streams of their
    own. A lost link keeps the list, and with it every stream.

### 3.5 Mutes, limits and the loss hint

- `SetListenerMutes` passes the set to the engine, which owns it, and drops what newly muted speakers left buffered at
  once. The service keeps no copy: `GetListenerMutes` reads the engine's, as section 2.5 kept or forgot it at the last
  join, so whatever shows the player whom they muted never shows a mute that no longer applies.
- **Names stay with their host.** Names reach the engine at once, whatever the session's state, and the engine ties them
  to the host that sent the last participant list (section 2.5). A reconnect to that host keeps them, through an
  in-place replacement too. A join that finds another host forgets them, and so does losing the link while a join's
  answer is still on its way. A name given after a join has found another host, but before that host's list arrives,
  goes at once: the session lists nobody then. A name meant for one host's player never silences whoever has
  that id on the next. An earlier version of the service held names back until this player was Ready in the session
  they were given in. It cleared the engine's set meanwhile and knew sessions only by id, so a change made while the
  link was down lost every mute when the host replaced its session before the player was back.
- **The wish to hear nobody**, `muteAll`, is the player's own and lasts until they change it. It names nobody, so the
  service gives it back wherever the engine forgot it: after a role change, and after a join that found a host without
  this player's record. While a join is under way the service makes it again, so the engine applies it to whichever
  session the join finds, and that host never relays the player a datagram they did not want. A set that also names
  people is left for the join to keep or forget, so whom the player muted on one host never reaches another.
- `SetServerMute` passes through for a host.
- `RateLimited` and `ServerMuted` results are counted, not reported, and the transmit indicator shows `MutedByHost`.
- `ReportTransportCounters(sent, lost)` takes the transport link's running packet totals. Every 250 packets yield a
  loss rate, rounded up and capped at 25%, which becomes the encoder's FEC hint. A rise applies at once; a fall halves
  the gap. The live sessions keep the transport's counters on the network thread and publish none to the main thread
  yet, so nothing calls it, and the codec's 10% default stands until they do.

### 3.6 Statistics

`GetStatistics()` reports:

- whether voice is on;
- capture, sending and receiving counters, with the frames the session took to send (`FramesSent`) and refused
  (`FramesRefused`), and the coded frames dropped when voice was turned off before they left (`FramesDroppedOff`);
- frames dropped because the bound on streams left their speaker none (`UnlistedFramesDropped`), and the streams
  ended because the participants did not list their speaker in time or a listed one needed the room
  (`UnlistedStreamsEnded`);
- the worker's steps, faults, slots run and slots dropped after a stall;
- the engine's `VoiceStatistics`;
- the encoder's loss hint;
- the input level;
- for each speaker, playback counters: decoded, recovered, concealed and faded frames, late frames, restarts, resets,
  extra and skipped pulls, cushions, frames trimmed from a stall's backlog, underruns, skips and the target delay.

They name participants, never addresses. `IsSpeaking(participant)` is true for 250 ms after a speaker was heard, for
talking indicators. Both read the speakers the main thread keeps, and refuse any other thread.

### 3.7 In the provider

Voice is off unless live multiplayer is switched on with `TOPIAFORGE_LIVE_MULTIPLAYER=1`, the switch that turns on live
sessions. When it is off, nothing is created: no codec, no voice options, no worker thread. When it is on, the provider
does four things:

- it gives its engine `EngineOptions.Voice` with an `OpusVoiceCodec`;
- it creates the `VoiceService` with that engine and the engine's clock, with voice off until the player turns it on;
- it pumps voice after the live sessions and the engine;
- it disposes voice before the live sessions and the engine shut down.

### 3.8 Tests and measurements

These run in the multiplayer test project, through the rig with the real codec on the provider's own copy of the
engine, deterministic except where noted:

- `VoiceServiceTests`:
  - four players all talking;
  - the voice shares;
  - a late listener;
  - a lone host coding nothing, and a host coding only for players who are Ready;
  - nothing captured, coded or sent until voice is turned on, and nothing more once it is turned off;
  - a reconnect and a restart, each started by its new generation;
  - listener and server mutes;
  - a returning speaker heard from the first frame while the session stream that announces the return is held back;
  - a worker that gives up after ten failed steps in a row, a step that works ending a run of them: voice reports it
    once, and every pump then drops what was queued for the worker;
  - the talking indicator and the statistics refused off the main thread.
- `VoiceSessionChangeTests`:
  - a listener moving to a host whose streams carry the same generations, with its worker stalled across the move, so
    the old session's last frames are still queued: none of them plays;
  - a host's own buffers dropped when its session is replaced in place;
  - the worker applying a reset among queued frames in their order, checked sample for sample;
  - the wish to hear nobody carried to another host, alone, and made again during the join, so the new host never
    sends a datagram;
  - mutes the engine keeps across an in-place replacement, honoured and reported;
  - a name muted while joining forgotten with another host, and one muted while the link was down kept by the host
    that kept the record;
  - mutes changed while the link is down or stalled kept through an in-place replacement, whether the reconnect or the
    recovered link brings it, with the mute made before the outage kept too;
  - names given while a stalled link moves to another host left behind, and a wish to hear nobody made while the link
    is down holding from the next host's first message;
  - names given with no host joined, at a first join or after a return to solo play, forgotten by the join;
  - a name given from the first host's list after the second host's `JoinAccepted`, while its large snapshot is still
    on the way, dropped at once, so the second host's own player is heard.
- `VoiceCaptureTests`:
  - voice activation and its hangover;
  - push-to-talk spurts, including a held key with no samples;
  - push-to-talk following the key, not the cadence of the capture calls;
  - silence under a held key;
  - stale capture after a stall;
  - capture keeping up at 10, 20, 30 and 60 frames a second;
  - capture and transmission refused off the main thread;
  - capture at 44.1 and 16 kHz.
- `VoicePlaybackTests`:
  - the ring's order, underruns, skips, flushes and overflow;
  - reads that allocate nothing, nor the writes beside them, through underruns, skips and a full ring, measured over a
    warmed loop as the best of three passes; reads that call nothing that can block (checked in the IL), and finish
    while another thread holds the stream's monitor;
  - 800,000 reads, each reader on its own thread, beside a writer that flushes between them, on real threads;
  - a device clock 0.5% fast and slow.
- `VoiceSpeakerBoundTests`:
  - a host naming 2,000 strangers, which never grows the streams past the bound, while a player who joins is heard;
  - voice before its speaker is listed, kept for the grace, and a stream nobody lists, ended after it.
- `VoiceStallTests`:
  - a second's stall of the listener's worker, its main thread or the host's, which leaves no lasting delay;
  - a paused reader, which hears nothing stale when it resumes.
- `VoiceProviderTests`:
  - the live switch, with voice off until it is turned on;
  - a real worker thread, the one test that waits on the wall clock, for at most five seconds;
  - the loss hint;
  - a code of its own for each engine diagnostic the provider passes on, the worker's failure included.

| Measurement | Result |
| --- | --- |
| Speech end to end, four players | 126 ms of delay; correlation 0.95 and 9.1 to 9.5 dB SNR against the input, the codec's own figure; nothing concealed, late or short |
| One talker's uplink at the 20 kbit/s target, 5 s | 29.8 to 29.9 kbit/s on the wire; the shaper charges 22.5 to 22.6 kbit/s of the client's 32.8, and 22.9 to 23.0 once the sequence takes three bytes |
| Three speakers relayed to one client | 90.6 to 90.7 kbit/s on the wire; the shaper charges 69.3 kbit/s of the 98.3 kbit/s share, and 70.1 to 70.2 once the sequence takes three bytes; the stream generation adds a byte to each relayed datagram |
| A second's stall mid-speech, of the listener's worker, its main thread or the host's | 126, 126 and 146 ms of delay 0.6 s after it ends, and 126 ms for all three 2 s after |
| Both players at 10, 15, 20, 30 and 60 frames a second | 366, 276, 226, 186 and 146 ms of delay; all capture kept but the first call's excess over 60 ms, and every coded frame sent; at 10 frames a second the first second conceals 10 frames while the jitter buffer adapts |
| Device clock 0.5% fast, 120 s | 29 extra pulls; never ran dry; at most 71 ms waiting |
| Device clock 0.5% slow, 120 s | 28 skipped pulls; never skipped audio; at most 117 ms waiting |
