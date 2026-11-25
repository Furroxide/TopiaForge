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

Pack the test probe from the repository with
`python -I tests/TopiaForge.MultiplayerAcceptanceMod/pack_acceptance.py --output <directory>`.
Supply the reviewed archive with `--package`. The helper stages the exact Gallery
Survival definition as an additional non-default theme for the non-distributable
probe. This is the sole R4-11 packaging exception. The ordinary packer validates
the staged source and writes the actual archive digest. The probe observes the supported public session and uses
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

### Manual Gallery visual gate

Use the same freshly packed `dev.topiaforge.multiplayer-acceptance` archive on both isolated visual profiles.
Create the empty file `BepInEx/TopiaForge/data/dev.topiaforge.multiplayer-acceptance/gallery-remote-preview.enabled`
before loading each visual profile. This mode selects the Gallery preview and skips the usual acceptance checklist.
Use separate profiles for the acceptance runner.

Once the normal session reports a ready listen host and exactly one admitted guest, press F7 on the host.
Each press opens a host-local fixture and sends the same safe composition to the guest through `IPlayer.Ui`.
The sequence starts at Theme packs and ends at Action slots, then wraps after 95 successful presses.
Each of the first eleven pages cycles Paper and HUD for the kit palette, the acceptance transport palette,
and authenticated Survival, in that order. Layout fit opens one HUD for each of those palettes.
The five interactive control pages each open a Paper Window in the kit and transport palettes.
Status, Countdowns, Key glyphs and Action slots each cycle kit Paper, kit HUD, transport Paper and transport HUD.

| Page | First press from a fresh fixture |
| --- | --- |
| Theme packs | 1 |
| Typography | 7 |
| Flex and padding | 13 |
| Wrap and grid | 19 |
| Stack and bounds | 25 |
| Cards and dividers | 31 |
| Icons | 37 |
| Images | 43 |
| Badges and pips | 49 |
| Foundation integration | 55 |
| Trust and fallback | 61 |
| Layout fit | 67 |
| Buttons | 70 |
| Text and choice inputs | 72 |
| Toggles, checkboxes and tabs | 74 |
| Key binding fields | 76 |
| Tooltips and list tiles | 78 |
| Status | 80 |
| Countdowns | 84 |
| Key glyphs | 88 |
| Action slots | 92 |

The Shared acceptance package owns its `transport-preview` descriptor and its copy of the CC0 sample bundle, receipt,
and license. The guest checks the authenticated package and descriptor for image references even in the kit variants.
The disposable acceptance build stages the exact Survival definition from the Gallery for the paired palette.
Record the F7 viewers as `listen-host` and `remote-guest`, and note the targeted delivery in the visual manifest.
Use the ordinary F8 Gallery on both peers for local Interaction states and Graph connections. Label the connected
guest's locally opened pages `guest-local`.

Set each viewer's resolution, scale, contrast, and motion preferences for the matrix before opening the fixture.
For all three Layout fit variants, start with each viewer at scale 1.0. After the wide HUD has opened, change each
viewer to scale 2.0 and capture its title and layout notice. Return to scale 1.0 and capture the restored content.
With `TOPIAFORGE_UI_GALLERY_CAPTURE=1`, collect the settled tree alongside the raster capture. Use F11 to repeat a
settled observation. The Key glyphs and Action slots pages also record automatically after each viewer changes input device. Use a gamepad control and wait for the automatic tree capture before using the keyboard again. Scroll tall pages in stages so the delivered evidence includes every authored row.
The managed test executable's `--ui-kit-gallery-fixtures` option checks the publisher sequence and paired
fixture lifetimes headlessly.

The opt-in package registers the same example bindings on both viewers. To capture the unavailable key field,
create an empty `gallery-remote-preview-no-bindings.enabled` in the guest's acceptance mod data directory
before that profile loads. Keep the host's example bindings so it can admit and publish the fixture.

#### Paired Status and Countdowns

For a shorter F7 cycle, add the empty `gallery-remote-preview-status-only.enabled` alongside
`gallery-remote-preview.enabled` before starting the host profile. The eight successful presses alternate
Paper and HUD through kit Status, transport Status, kit Countdowns, and transport Countdowns.
Press 9 returns to kit Status Paper. Let each publication settle before pressing F7 again.

Use the two resolutions and accessibility settings of the phase's visual gate. At each setting, capture the static
Status page on both peers. Check the captured progress percentages, Warning and Cross cues, and the text crossing
the filled and empty sides of each stat bar. Keep the exact acceptance archive hash and check the guest tree's
`packageSha256`, `theme`, and `descriptorSha256`.
Both peers should use the requested palette with their own scheme and accessibility settings.

Start each Countdowns run with a new F7 publication. Have both viewers ready for the automatic initial tree
capture. Record continuous video or take frames while both timers advance. Press F11 on each viewer for a tree
in each state and pair it with a frame showing the same digits. Use the displayed values as the source of truth.
The expected states for `Next transfer` (`timer-transfer`) are:

| Reading | Expected cue | When to request a reading after F7 |
| --- | --- | --- |
| `0:12` or `0:11` | None | Initial automatic capture |
| `0:10` through `0:06` | Warning | About 3 seconds |
| `0:05` through `0:01` | Cross | About 8 seconds |
| `0:00` | Cross, stable at zero | About 13 seconds |

Repeat the publication and use the longer `Seconds` row if the first reading settles after the short timer
enters Warning. Its Warning, Cross, and expiry readings are roughly 15, 20, and 25 seconds after F7.
The fixture floors the starting clock and displays whole seconds rounded up. Frame timing and guest clock
sampling can put the two peers one displayed second apart at a boundary. Verify the displayed threshold and its
cue on each viewer. Compare the digit alignment in the three formats. Confirm that `Elapsed` starts at
`0:00` and that `Next transfer` stops there without pulsing or an author update.
F7 replaces the fixture and starts fresh deadlines. F11 samples the surface already open.

#### Retained countdown after a real local epoch change

In a separate solo profile, open the ordinary F8 Gallery, select HUD, and open Countdowns in its default Survival theme.
Collect the native tree and frame while the hour and minute rows still show nonzero values. Keep the Countdown HUD
open. In the manager's GAMEMODES tab, select the fixed Multiplayer Acceptance/Open Sandbox target and choose PLAY
without opening the session to multiplayer. After the new solo world is running, use F11 and capture the same HUD
again without reopening the Gallery. If the profile already runs that target, a second PLAY uses the same world
launch path. Compare the title, theme, captions and frame across both captures. The long rows must now show
`0:00` and `0:00:00`, `Seconds` must show `0`, and `Next transfer` must show `0:00` with its Cross.
The long values distinguish epoch retirement from ordinary expiry. File the captures as `solo`.

#### Retained countdown on a paired map replacement

Use a disposable listen pair with an already-admitted local map launch target. Add the empty `gallery-remote-preview-retained-countdown.enabled`
beside `gallery-remote-preview.enabled` on the host before loading the profile. This mode selects the transport
Countdowns HUD even if the short-cycle marker is present. Every successful F7 press starts a new run. The ordinary F7 cycle still retires
its pair on a session replacement.

Open the HUD with one F7 press after both peers are ready. Save the host and guest captures while the long
rows are nonzero. On the same listen host, choose PLAY for the admitted local map in GAMEMODES. Use the normal
map launch that keeps the listen process and guest connected. Once the guest follows the replacement map, use
F11 on both peers. Keep the same acceptance archive installed and leave the original HUD in place.

The host should still show formatted zeros in the old HUD. The guest should recover the same title, theme,
captions and frame with the plain formatted zero text carried by the replacement stream. The text uses the
countdown's base tone, with no critical paint or Cross. Confirm `Seconds 0`,
`Minutes and seconds 0:00`, `Hours, minutes and seconds 0:00:00` and `Next transfer 0:00`.
Check the guest's exact archive and descriptor hashes across both observations. A disconnect, a guest change,
or a second replacement retires the paired fixture. If the local map cannot replace the session in place,
record the paired epoch row as pending and keep the independent solo result.

#### Paired overlays, stingers and toasts

Add `gallery-remote-preview-presentation-only.enabled` beside the main F7 marker on the host before
loading the profile. Keep the Status-only and retained Countdown markers absent for this run. The presentation
cycle has 30 successful presses and wraps at press 31. It uses the same ready listen host and single admitted guest.

| Fixture ID | Kit press | Transport press |
| --- | ---: | ---: |
| `gallery-overlay-passive` | 1 | 16 |
| `gallery-overlay-dismissible` | 2 | 17 |
| `gallery-overlay-buttononly` | 3 | 18 |
| `gallery-overlay-stack` | 4 | 19 |
| `gallery-stinger-surface` through `gallery-stinger-danger` | 5–13 | 20–28 |
| `gallery-stinger-queue` | 14 | 29 |
| `gallery-toast-stack` | 15 | 30 |

Record the fixture ID, palette, and each viewer's resolution and accessibility settings. Use the fixed order
Surface, Raised, Sunken, Selected, Primary, Accent, Success, Warning, and Danger for the nine stinger rows.
The host log records a fixture ID and palette after both public UI routes accept the step. Use the rendered result
and settled tree on each viewer as the visual evidence. Let every stinger finish before advancing F7.

For the passive overlay, check that gameplay input still works. On the dismissible overlay, use Back on the guest
and confirm the host's copy stays open. On the button-only overlay, use B, then try Escape and controller Start
while the same overlay is open. Capture the game's pause above the overlay and verify focus returns on resume.
Use the enabled button to close only the viewer's copy. Repeat the ordinary controls on the other viewer.
In the stack fixture, reveal the lower overlays in order and read `Supplies loaded.` on the existing
`gallery-presentation-hud` after all overlays clear.

On the queue step, capture `Depot open`, `Water ready`, `Weather report`, then `Lanterns lit` on both peers.
Each plays once. The duplicate Water announcement coalesces and `Gate closing` is refused by the full queue.
Record a tree alongside the displayed frame before advancing. Repeat the timed observations with reduced motion.
On the toast step, collect the four system toasts over the themed HUD. Confirm kit paint, the plain messages and
the Check, Warning and Cross glyphs on the corresponding tones.

Use F8 on both viewers for the Survival sample in the phase matrix. For a live cleanup check, end or replace the
session with the stack still open. Both viewers' owned surfaces should retire, and a new publication requires
another host F7 press on a ready pair. The stinger and toast steps send timed events and new peers receive no replay.

#### Art fallback

Compare the kit HUD variant of **Trust and fallback** on a ready pair. To reproduce unavailable artwork, use a fresh
isolated pair that has admitted its identical archives and has yet to open a preview. On the guest only, hold an
exclusive Windows read handle (`FileAccess.Read`, `FileShare.None`) on the installed acceptance package's
`ui/art/preview.bundle`. Keep the handle open through the fallback capture. The bundle bytes and package identity stay
intact. Press F7 through the diagnostic's kit HUD variant. Compare the host's picture and guest's placeholder at the
same viewport and profile. The placeholder should retain the supply card's frame and readable label. The decorative
marker should have no fallback text. Dispose the handle and use a fresh pair for the ready follow-up.

#### Loader trust isolation

Use an isolated Windows guest profile and a coordinator-owned local host reachable by typed address. Keep the guest
out of a session for this check. Before starting that guest, take the SHA-256 of its existing
`BepInEx/TopiaForge/data/io.github.furroxide.topiaforge.multiplayer/multiplayer/known-hosts.json` and hold an exclusive
Windows read handle on that file. Use a disposable copy of a test profile with an existing known-hosts record.

Open F8 Gallery, select HUD, and open **Trust and fallback** in its default Survival theme. Leave this authored
HUD visible and go to the manager's **MULTIPLAYER → CONNECT** tab. Enter the test host's local address and use the
typed-address join check. When the provider returns `KnownHostsUnreadable`, the loader opens **Trust this server?**.
Observe that the loader prompt stays in the kit Paper palette, stays above the Gallery HUD, and owns focus and Back.
Capture the prompt with the visible Gallery area and save the Gallery's settled tree separately. Use Cancel or Back to
dismiss the prompt. Dispose the file handle and verify the record's hash. Before exporting the raster, check that the
typed address and any other private connection fields are outside the capture.

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
