# Scoreboard and avatar names

Hold Tab in a live gameplay session to see its players. Releasing Tab, Escape,
typing, a key capture, a modal or leaving gameplay closes the scoreboard. A held
key cannot reopen it after another surface closes; release and press again.
The action uses the existing input registry and its per-binding ownership rules.
With a row control selected, kit navigation owns navigation keys and controller
controls. The scoreboard changes no native action map.

The HUD view reads the canonical session roster and the Players API's scoreboard
baseline. Names and values are bounded, redacted and rendered literally. Rows
show a host badge, talking state, bucketed connection quality and local listening
controls. The list scrolls; up to 64 game columns appear four at a time, with
numeric sorting by the author's sort key. Missing values sort last. Equal values
sort by canonical participant ID for a deterministic result.

Kick and ban use the existing destructive modal. Every action resolves the
retained row's participant ID and player key in the current ready session. Host
actions also recheck the current provider role after confirmation. Changed
sessions, departed players and reused IDs cannot redirect an old action.

Local mute shares VOICE's stable-key mute book and reaches the voice SPI through
the existing voice runtime. The SPI supplies playback streams; its contract has
no per-speaker gain member. A bounded local volume book applies that player's gain
at the existing stream emitter, multiplied by the master voice volume. It follows
the same stable-key/session-only fallback as local mute, lives only in memory,
and never enables voice or opens a microphone. Server mute uses the provider's
authenticated moderation path and affects both chat and voice.

Avatar names read the presence nametag seam and use the kit's pooled nametag
layer. Up to 32 labels follow avatar head anchors; the renderer provides distance
fade, projection, high contrast, UI scale and reduced motion. Display slots carry
no action authority. An absent avatar or gameplay scene hides its slot. This
does not change presence's documented first-pose anchor limitation.

The SESSIONS gallery page opens the production view with host, client, sixteen
players, sixty-four columns, literal markup, reconnecting and empty states.
ONLINE's NAME TAGS page exercises the same renderer's near/far limits, moving
anchors and literal names. Offline tests cover production presenters, voice
playback, the nametag feed and provider moderation. Native qualification must
still check both supported resolutions, scale 1.5, keyboard/controller focus,
two machines, reconnects and the complete rendered frame's allocations.
