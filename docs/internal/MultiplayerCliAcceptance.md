# Multiplayer CLI and listen-pair acceptance

`servers` and `host` use the launcher's data and domain layers. There is no CLI
join flow yet. The command count increases from 27 to 29; the single-instance
acceptance specification, its model and release qualification counts are unchanged.

## Server browser

```text
topiaforge servers [--source all|internet|lan] [--game-build build]
  [--protocol-major number] [--after server-id] [--wait-seconds 0..30] [--json]
```

The default snapshots one Internet page and listens for LAN sessions for three
seconds. Internet browsing uses the detected install's build, or the explicit
`--game-build`. Protocol major defaults to 1. An unavailable service does not
disable LAN browsing. Stored LAN-off and unreadable preferences stay off.
No install is needed for LAN browsing. A missing Internet build asks for the filter.

JSON always contains exactly `schemaVersion` (1), `sources`, `retryAfterSeconds`,
`next`, and `servers`. `sources` holds one status string for each requested source,
`internet` and `lan`, and nothing else; an unrequested source is omitted.
`retryAfterSeconds` is the integer delay the service named before Internet
browsing can be retried, or null, including when Internet browsing was not
requested. `next` is the next server-id cursor, or null. Each server has
`serverId`, `name`, `source`, `players`, `maxPlayers`, `passwordRequired`, `voice`,
`gamemode`, `world`, `gameBuild`, `runtimeVersion`, `pingMilliseconds`, and
`readiness`; absent optional values are null. Rows sort by source then server id.
The same server heard from both sources keeps both rows. `next` is passed as
`--after` for another page. No endpoint, transport key or descriptor is projected.
Notices go to stderr, so stdout remains one JSON document. Exit 0 means at least
one requested source was available, even with zero sessions; exit 1 means none
was available. The per-source statuses expose partial availability.

Internet statuses are `available`, `notConfigured`, `buildRequired`, `offline`,
`unreachable`, `unavailable`, `turnedOff`, `rateLimited`, `notFound`, `refused`,
`invalidAnswer`, and `descriptorInvalid`. LAN statuses are `stopped`, `disabled`,
`listening`, `unavailable`, `portInUse`, `permissionDenied`, `failed`, and
`settingsUnreadable`. A LAN cleanup failure is a stderr notice only; it adds no
JSON field and does not change the snapshot's availability.

## Hosting

```text
topiaforge host [--game-dir path] [--profile id] [--target id | --main-menu]
  [--name text] [--visibility lan|private|invite|public] [--port 0..65535]
  [--max-players 2..16] [--password] [--serve-packages]
  [--allow-direct] [--yes] [--wait-seconds 1..300]
```

The selected launcher profile supplies content and the saved launch selection.
The shared multiplayer store supplies display name, hosting defaults, LAN, UPnP
and previously consented voice preferences. The game's preference journal is
folded back only while the game is closed. A second round after consent detects
changed preferences. The repository checks the profile again under its launch lease.
The CLI never stops a running game to host.

The approved disclosure is shown before first hosting. Direct hosting requires
`--allow-direct`, the approved direct disclosure and per-session consent.
`--yes` permits nonterminal consent after printing the disclosures; with a
terminal it never skips the questions. Voice stays off without stored consent.
`--serve-packages` requires the launcher's developer mode. Passwords are entered
in game, never accepted or staged on the command line: with `--password`, the
command says that the game asks for it, and waits up to 300 seconds for hosting
unless `--wait-seconds` is given. A Safe Mode profile never hosts.
`--allow-host-packages` belongs to the future join verb and is refused here.

| Stage | Meaning and recovery |
| --- | --- |
| TFHOST100 | Options, install or profile unavailable; check command help and Setup. |
| TFHOST110 | Preferences, consent or developer mode refused; review the choice and retry with the game closed. |
| TFHOST120 | Preflight or Internet service unavailable; repair the profile or choose an available connection mode explicitly. |
| TFHOST130 | Process start failed; close conflicting copies and repair the profile. |
| TFHOST140 | Runtime confirmation, invite or hosting failed; follow the specific remedy or check the running game before retrying. |

The monitor subscribes before spawning and correlates request, profile revision,
install, package digest, process identity and multiplayer intent. It needs both
successful outcome V2 and hosting progress V2. Public/invite visibility continues
waiting for the invite; the code and its `topiaforge://join/` link print once.
LAN/private hosting can succeed without an Internet invite. Exit 0 is confirmed,
1 is failed, 2 is invocation/consent misuse, 3 is unconfirmed, and 130 is cancelled.
Timeout and cancellation leave the game unchanged. Raw exceptions and runtime
failure detail are never printed, since they may contain private data.

## Listen-pair suite

```text
topiaforge acceptance run --suite multiplayer --dry-run [--case id ...]
topiaforge acceptance run --suite multiplayer --game-dir source
  --isolation-record host-record --guest-isolation-record guest-record
  --package probe --output host-evidence [--timeout-seconds 30..3600] [--all]
```

The inventory is `tests/multiplayer-live-acceptance.json`: 25 cases, 49 required
peer observations, including 13 manual cases. A dry run performs no installation,
network activity, configuration write or process start, and is explicitly not live
evidence. Unknown suites never fall through to single-instance acceptance.

Both records use the existing acceptance-isolation schema and must admit the
current separate QA Windows identity. They identify six nonoverlapping writable
roots: two game overlays, two launcher data folders and two evidence folders.
Use copied executables, private BepInEx trees, and the S1 junctions only for the
read-only `Robotopia_Data` and `MonoBleedingEdge` trees. Writable roots and their
ancestors remain ordinary directories. Both processes share the isolated QA
user's native Unity persistence; this does not claim separate OS profiles for
the peers. The normal player's persistence remains outside both admissions.

The harness checks the exact probe archive and validates both admissions before
writes, prepares both runtimes, creates a fresh challenge, then launches host
and guest in that order. The probe comes first, so a missing probe is refused
as `TFMPACCEPT110` before any QA layout is read. The harness's own refusals
(`TFMPACCEPT100` to `TFMPACCEPT140`) print as they are; any other failure prints
one generic sentence, since admission errors can name private QA roots. Both
use V5 host-session and join-session requests, each hashed into its private
challenge sidecar. The same admission binds the measured identity and exact roots.
Cleanup attempts both original receipts even if one fails. Unconfirmed cleanup
prevents success. Acceptance never replaces an already running process, and
profile arguments or environment overrides remain forbidden. Ordinary single-instance
acceptance still writes V4. No guest config names the host's evidence root.
The original process receipts must remain live during collection and at the final
evidence check; an exited or unverifiable peer cannot be replaced by later markers.

### Game-side prerequisite

Build and pack `tests/TopiaForge.MultiplayerAcceptanceMod`, then supply its reviewed
archive with `--package`. The probe observes the supported public session and uses
`Context.Ui` for a Paper window with scrollable manual checks, toggled by F9. It
does not host, join, resolve addresses or write outside its mod-owned services.
The runner owns those operations through the production launcher and V5 pipeline.

- Package id: `dev.topiaforge.multiplayer-acceptance`.
- The host starts the `.session` target with the Worlds Free Play mode and Open
  Sandbox. The guest starts at the main menu and follows through normal joining.
  The runner confirms matching outcome V2 and hosting progress V2, verifies the
  signed descriptor against the confirmed server identity, then launches the guest
  with an exact loopback target and confirms its outcome and in-session progress
  against that target's server identity.
  The local port is selected before launch; a race for that port fails explicitly.
- Schema-1 config `value`: `challenge`, `peer` (`host` or `guest`), `topology`
  (`listen-pair`), `requiredCases`, and `checks` containing that peer's case ids,
  modes and instructions. Each peer reads only its own config and writes through
  its attributed logger. Voice and router forwarding are off for this base pair.
- Both peers must emit `TF-MP-SESSION|READY|challenge|peer` after the public session
  reports the correct local role and at least two connected participants.
- Attributed manager log markers, with no extra suffix:
  `TF-MP-ACCEPT|PASS|challenge|peer|case-id|automatic` at INFO, or
  `TF-MP-ACCEPT|FAIL|challenge|peer|case-id|automatic` at ERROR. Manual cases use
  `manual` only after the tester has performed and confirmed the stated check.
- `NOT-RUN` at INFO explicitly leaves a check incomplete. The base probe supports
  host-start plus in-session manual observations. Other automatic scenarios and
  voice, kick/ban and host-quit need their own driver. Invite and relay are marked
  as service-dependent in the dry-run inventory. They cannot pass by observing a
  local pair. The result records `notRun`, `notRunReasons` and `bothInSession`;
  any not-run row prevents overall success. Use `--case multiplayer.host-start`
  for the base transport check; the complete release suite needs the later drivers.
- The harness requires each case's listed peers, rejects replayed challenges,
  wrong logger sources and wrong manual/automatic modes, and keeps any failure.
  Both fresh `last-run.json` records must show the exact installed archive and
  critical-file receipts loaded, with no root startup error. A previous startup
  identity or a completion timestamp outside the current run cannot qualify.
  Log polling reads at most 64 KiB at a time and refuses lines over 8 KiB.

`multiplayer-acceptance-result.json` is private QA evidence, including both
runtime isolation acknowledgements. Do not publish it or raw game logs; use
reviewed hashes. Source-only tests exercise synthetic peers and cannot prove
the multiplayer transport. Internet, real input, rendered
state and microphone checks still need a live run.

## Joining

`topiaforge join <code|link|host:port>` uses the same join reducer, verified descriptor planner, exact-archive
installer, server profiles and V5 launch gateway as the launcher. It prints the same consent facts and warnings:
mods, source, full SHA-256, size, capabilities, privacy, voice, password handling and profile changes.

- Without an interactive input console, `--yes` is required. With one, `--yes` never skips disclosure or consent.
  A detached Windows character handle that cannot answer a console-mode query is noninteractive too.
- `--allow-direct` asks the approved direct-connection prompt and applies to this request only. A public address
  or non-local hostname is refused before contact without that flag; Hide my IP stays on for other sessions.
- `--allow-host-packages` requires developer mode and the separate host-served warning and acknowledgement.
  It does not make packages from a service-resolved host eligible; the shared planner still governs them.
- `--game-dir` selects an install. `--wait-seconds 1..300` bounds the runtime-confirmation wait (default 300 seconds,
  including time for a password entered only in Robotopia). No password is accepted on the command line.
- Cancellation waits for an install or profile transaction to settle. A live join not yet taken may be withdrawn;
  a spawned game remains running. Timeout is unconfirmed, never success, and tells the player to check the game.
- An admission change replans once. Identical consent is reused; changed packages require fresh consent.
- No provider error, observed address or local path is printed. The command follows only its returned process
  receipt and intent. It does not print an invite or a target supplied by the service.

| Stage | Meaning |
| --- | --- |
| `TFJOIN100` | Options, consent requirement or local setup could not be used. |
| `TFJOIN110` | Preferences, target resolution or descriptor verification. |
| `TFJOIN120` | Exact-package and admission planning. |
| `TFJOIN130` | Consent, host-package acknowledgement or changed preferences. |
| `TFJOIN140` | Downloading and validating exact archives. |
| `TFJOIN150` | Installing or rolling back server packages. |
| `TFJOIN160` | Saving the derived server profile. |
| `TFJOIN170` | Preparing or starting the game. |
| `TFJOIN180` | Connecting, receiving the world, confirmation or an unconfirmed wait. |

Exit codes are 0 for confirmed join, 1 for a refusal or failed step, 2 for invalid options or missing consent flags,
3 for an unconfirmed started game, and 130 for cancellation. Each failure carries a next step. `servers --json`
remains the machine-readable discovery interface; join consent is human-readable text.
