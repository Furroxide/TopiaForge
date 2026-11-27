# Gamemode communication routing

`TopiaForge.Mods.Multiplayer` 0.2.0 lets a gamemode server control who receives voice and text chat.
The host authorizes each delivery, including its own microphone and chat display. Clients send no audience or group
claims. This API does not expose audio samples, transcripts, network addresses or other players' private messages.

Call `session.Context.RouteCommunications(policy)` from the server controller. It returns an
`OperationResult<ICommunicationRouting>`. Keep the successful handle and call `SetPolicy` when membership or the phase
changes. Each policy atomically replaces the complete audience. The context owns the handle automatically; disposing
it early restores ordinary routing. Participant controllers, world owners and ordinary mod contexts cannot acquire it.
Only one handle may be active. Without a multiplayer provider, acquisition returns `Unavailable`.

```csharp
// Assign these participant ids from the server's player directory and authoritative game state.
var members = new[]
{
    new CommunicationMember(firstPlayer.Id, 1),
    new CommunicationMember(secondPlayer.Id, 1),
    new CommunicationMember(observer.Id, 2),
};
var round = new CommunicationPolicy(members, new[]
{
    new CommunicationRoute(1, 1, maximumDistance: 15),
    new CommunicationRoute(1, 2), // observers hear active players globally
    new CommunicationRoute(2, 2), // observers speak only to observers
});
var opened = session.Context.RouteCommunications(round);
if (opened.TryGetValue(out var routing))
{
    // During a discussion, active players hear one another regardless of distance.
    routing.SetPolicy(new CommunicationPolicy(members, new[]
    {
        new CommunicationRoute(1, 1),
        new CommunicationRoute(1, 2),
        new CommunicationRoute(2, 2),
    }));
}
```

Groups are opaque integers 1–16. A player belongs to one group; the host decides membership from its own game state,
including alive/dead transitions. Rules are directed: allowing 1 → 2 does not allow 2 → 1. Missing players and missing
routes are denied. An empty route list silences everyone, including the host. For a private team, assign a group and
allow only its self-route. Update membership for joining players before they can communicate under the policy.

The same rules govern chat and voice. Chat echoes also require a route to the sender's own group; private messages are
not appended to an unauthorized host's chat display. The host process remains trusted and necessarily receives client
uplinks to authorize them. This is audience isolation from other players, not encryption against the host.

Zero maximum distance means global delivery. Positive distances up to 1,000 metres require fresh, validated positions
in the current host world. Missing, stale or invalid positions deny proximity delivery. Global voice carries a
host-only non-positional playback flag so a discussion remains audible beyond ordinary voice attenuation distance.
Proximity voice keeps the existing positional fade (full volume within 3 m, silent beyond 40 m); the policy distance
is an authorization ceiling, not an audio-volume control. Chat uses the same distance ceiling without attenuation.

Policies copy their input lists and reject duplicate members, duplicate routes, invalid groups and invalid distances.
Each holds at most 64 members and 256 routes. Existing voice rate/bitrate limits, automatic moderation, listener mutes,
chat throttles, payload bounds and per-recipient backpressure remain effective. A policy grants no microphone consent.

Session replacement retires the handle and discards held voice and inbox frames. Reusing the old handle returns
`Cancelled`; disposing it cannot remove a newer handle. Disconnected or unready players cannot send or receive through
the policy. A reconnect within the same session retains membership when the host retains that participant id.
Stopping the gamemode restores ordinary routing. Already authorized traffic in transport or playback buffers cannot
be recalled by a policy update; it drains within the existing bounded queues.

Ordinary sessions keep their existing wire and playback behavior. Routed global voice adds relay flag `0x04`, which is
rejected on uplinks. Older receivers reject that flag rather than playing it incorrectly; games using this API require
TopiaForge 0.2.0 on every participant. Group assignments and route lists never travel to clients or diagnostics.
