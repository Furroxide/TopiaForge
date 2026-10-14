# Multiplayer privacy text — approved wording

Status: Approved · Date: 2026-10-14 · Owner: maintainer

The live-multiplayer plan makes the player-facing wording on address exposure and microphone use a release gate,
approved by the maintainer as owner (hard constraint 5). This page holds that wording, approved as written on
2026-10-14. The game's screens, the launcher and the player guides use these strings word for word. Any change to
their meaning needs a fresh approval before it ships.

The rules behind the wording are in `PrivacyAndCapabilities.md` ("Peer address exposure in player-hosted sessions"),
and the mechanics are in `MultiplayerLiveSessions.md` (sections 17 to 22).

## First time a player hosts or joins

Shown once, before the first host or join, and again whenever the disclosure's version changes.

**Title:** Your address and multiplayer

> When you play directly with others, they can see your IP address and you can see theirs. An IP address shows
> roughly where you are and which internet provider you use, and someone could use it to disrupt your connection.
>
> Hide my IP is on by default: your sessions go through a relay or stay on your local network, so nobody else sees
> your address. A relayed session can be a little slower.
>
> You can allow a direct connection for one session when you choose to. TopiaForge never turns it on for you.

**Button:** I understand

## When a session needs a direct connection

Shown each time, for one session only. Cancelling leaves Hide my IP on and the join or host does not happen.

**Title:** Allow a direct connection?

> This session can only be reached directly. If you continue, the other players in it will see your IP address for
> this session only. Hide my IP stays on for every other session.

**Buttons:** Allow for this session · Cancel

## First time a player turns on voice chat

Shown before the microphone is used for voice chat. The choice is stored, can be withdrawn in Voice settings, and is
asked again whenever its version changes.

**Title:** Voice chat

> Voice chat sends what your microphone picks up to the other players in the session, through the host's game.
> TopiaForge does not record it. Voice stays off until you turn it on, you can choose push-to-talk or voice
> activation, and you can turn it off at any time in Voice settings.

**Buttons:** Turn on voice chat · Not now

## Launcher consent sheet

One privacy line and one voice line, chosen by the session's settings.

| Setting | Line |
| --- | --- |
| Hide my IP on | Privacy: Hide my IP is on, so the host won't see your address. |
| Direct connection allowed for this session | Direct connection: the host will see your IP address. |
| Voice chat off | Voice chat: off |
| Voice chat on | Voice chat: on, so players in this session hear your microphone. |
