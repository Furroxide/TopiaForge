# Multiplayer nametags

The manager projects the presence service's avatar head anchors through
`TopiaForgeNametagLayer`. Names appear automatically for live hosts and clients
in gameplay, independently of interactive overlays. Gamemode authors do not
register a second name list or update labels each frame.

The pool holds 32 display-only slots. A slot is not a player identity and cannot
authorize player actions. Every frame reads the current anchor even when the
roster version is unchanged. Live anchors read the head during kit projection,
after presence interpolation in LateUpdate. The kit driver runs at execution
order 10000, after the core gameplay loop at its default order 0. Replaced names
pass through the existing address
redactor and single-line, 32-character display bound. The kit renders markup
literally and owns distance fading, camera projection, UI scale and high contrast.
The presence anchor already includes head clearance, so the label adds no offset.

The pool is created on the first visible avatar. Gameplay departure, provider
loss and roster shrink hide unused slots. Returning players reuse the pool.
Its HUD layer persists across scenes; the current presence source determines
which avatars remain visible. Unexpected layer loss disposes the old host and
rebuilds from current presence on the next frame. Disposal prevents resurrection.
Each frame runs through the manager's existing failure guard.

The existing UI gallery's NAME TAGS section exercises the same kit primitive,
including near, fading, offscreen and long-name cases. Runtime qualification must
also cover live avatars and session teardown at 2560x1440 and 1920x1080, UI scales
1.0 and 1.5, high contrast and reduced motion. Managed tests cover bounded pools,
source changes, lifecycle recovery, Update/LateUpdate ordering for movement,
teleport and crouch, and exact-zero settled dispatch and live anchor reads;
they do not measure native rendering allocations.
