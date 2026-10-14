/**
 * The signaling room inside a host session. Sockets use the hibernation API: the runtime keeps each socket's small
 * attachment in memory, and nothing about signaling is ever written to storage. The host's socket lives as long as its
 * session; a peer's lives at most PEER_SOCKET_SECONDS.
 *
 * Privacy is enforced here, not trusted to clients: a relay-only side's frames reach the other side only after
 * `permittedCandidates` has removed every candidate that could reveal its address.
 */
import { stringify, type JsonObject } from "../shared/canonical-json";
import { candidatesOf, forwardedFrame, permittedCandidates, readSignal, MAXIMUM_FRAME_BYTES, type SignalRole } from "../shared/signaling";

export const MAXIMUM_PEERS = 32;
export const PEER_SOCKET_SECONDS = 15 * 60;
const RATE_WINDOW_SECONDS = 10;
const RATE_WINDOW_FRAMES = 40;
const MAXIMUM_INVALID_FRAMES = 5;

export const CLOSE = {
  normal: 1000,
  unsupported: 1003,
  tooLarge: 1009,
  invalid: 4400,
  rateLimited: 4408,
  replaced: 4409,
  sessionEnded: 4410,
  expired: 4411,
} as const;

/** Kept with each socket by the runtime, in memory only. */
export interface SocketState {
  readonly role: SignalRole;
  readonly peer: string | null;
  readonly relayOnly: boolean;
  readonly self: string | null;
  readonly closeAt: number | null;
  readonly windowStart: number;
  readonly windowFrames: number;
  readonly invalidFrames: number;
}

export interface RoomContext {
  readonly ctx: DurableObjectState;
  readonly now: number;
  readonly fingerprint: (address: string) => Promise<string>;
}

export function stateOf(socket: WebSocket): SocketState | null {
  return socket.deserializeAttachment() as SocketState | null;
}

export function hostSocket(ctx: DurableObjectState): WebSocket | null {
  return ctx.getWebSockets("host")[0] ?? null;
}

export function peerSocket(ctx: DurableObjectState, peer: string): WebSocket | null {
  return ctx.getWebSockets(`peer:${peer}`)[0] ?? null;
}

export function send(socket: WebSocket, frame: JsonObject): void {
  try {
    socket.send(stringify(frame));
  } catch {
    // The socket closed underneath us; its close handler tidies up.
  }
}

export function close(socket: WebSocket, code: number, reason: string): void {
  try {
    socket.close(code, reason);
  } catch {
    // Already closed.
  }
}

/** Accepts the host's socket, replacing any earlier one, and tells it who is waiting. */
export function acceptHost(ctx: DurableObjectState, now: number, relayOnly: boolean, self: string | null): Response {
  for (const old of ctx.getWebSockets("host")) close(old, CLOSE.replaced, "replaced");
  const [client, server] = Object.values(new WebSocketPair()) as [WebSocket, WebSocket];
  ctx.acceptWebSocket(server, ["host"]);
  server.serializeAttachment(initialState("host", null, relayOnly, self, null, now));
  const peers = ctx
    .getWebSockets("peer")
    .map(stateOf)
    .filter((state): state is SocketState => state !== null && state.peer !== null)
    .map((state) => ({ peer: state.peer!, relayOnly: state.relayOnly }));
  send(server, { type: "welcome", role: "host", peers });
  for (const peer of ctx.getWebSockets("peer")) send(peer, { type: "host-status", online: true, relayOnly });
  return new Response(null, { status: 101, webSocket: client });
}

/** Accepts a peer's socket, replacing an earlier one for the same ticket; null when the room is full. */
export function acceptPeer(
  ctx: DurableObjectState,
  now: number,
  peer: string,
  relayOnly: boolean,
  self: string | null,
  ticketExpiresAt: number,
  hostRelayOnly: boolean,
): Response | null {
  const replaced = ctx.getWebSockets(`peer:${peer}`);
  if (ctx.getWebSockets("peer").length - replaced.length >= MAXIMUM_PEERS) return null;
  for (const old of replaced) close(old, CLOSE.replaced, "replaced");
  const [client, server] = Object.values(new WebSocketPair()) as [WebSocket, WebSocket];
  ctx.acceptWebSocket(server, ["peer", `peer:${peer}`]);
  server.serializeAttachment(initialState("peer", peer, relayOnly, self, Math.min(now + PEER_SOCKET_SECONDS, ticketExpiresAt), now));
  const host = hostSocket(ctx);
  send(server, { type: "welcome", role: "peer", peer, hostOnline: host !== null, hostRelayOnly });
  if (host !== null) send(host, { type: "joined", peer, relayOnly });
  return new Response(null, { status: 101, webSocket: client });
}

/** Handles one frame from either side. */
export async function onFrame(room: RoomContext, socket: WebSocket, frame: string | ArrayBuffer): Promise<void> {
  const state = stateOf(socket);
  if (state === null) return close(socket, CLOSE.invalid, "unknown");
  if (typeof frame !== "string") return close(socket, CLOSE.unsupported, "text only");
  if (frame.length > MAXIMUM_FRAME_BYTES) return close(socket, CLOSE.tooLarge, "too large");
  if (state.closeAt !== null && room.now >= state.closeAt) return close(socket, CLOSE.expired, "expired");

  const sameWindow = room.now - state.windowStart < RATE_WINDOW_SECONDS;
  const windowFrames = sameWindow ? state.windowFrames + 1 : 1;
  const counted: SocketState = { ...state, windowStart: sameWindow ? state.windowStart : room.now, windowFrames };
  if (windowFrames > RATE_WINDOW_FRAMES) {
    send(socket, { type: "error", code: "rate-limited" });
    return close(socket, CLOSE.rateLimited, "rate limited");
  }

  const message = readSignal(state.role, frame);
  if (message === null) {
    const invalidFrames = counted.invalidFrames + 1;
    socket.serializeAttachment({ ...counted, invalidFrames });
    send(socket, { type: "error", code: "invalid-message" });
    if (invalidFrames >= MAXIMUM_INVALID_FRAMES) close(socket, CLOSE.invalid, "invalid frames");
    return;
  }
  socket.serializeAttachment(counted);

  const target = state.role === "peer" ? hostSocket(room.ctx) : peerSocket(room.ctx, message.peer!);
  if (target === null) {
    send(socket, { type: "error", code: state.role === "peer" ? "host-offline" : "unknown-peer" });
    return;
  }
  const offered = candidatesOf(message);
  const kept = await permittedCandidates(offered, { relayOnly: state.relayOnly, selfFingerprint: state.self }, room.fingerprint);
  if (kept.length < offered.length) send(socket, { type: "notice", code: "candidates-withheld", withheld: offered.length - kept.length });
  // A trickled candidate that may not leave is dropped whole; every other frame goes on with what may.
  if (message.type !== "candidate" || kept.length === 1) {
    send(target, forwardedFrame(message, state.role === "peer" ? state.peer : null, kept));
  }
  if (message.type === "bye") close(state.role === "peer" ? socket : target, CLOSE.normal, "bye");
}

/** Tells the other side that a socket went away. */
export function onClosed(ctx: DurableObjectState, socket: WebSocket): void {
  const state = stateOf(socket);
  if (state === null) return;
  if (state.role === "peer" && state.peer !== null) {
    // A replacement socket for the same ticket keeps the peer present.
    if (ctx.getWebSockets(`peer:${state.peer}`).some((other) => other !== socket)) return;
    const host = hostSocket(ctx);
    if (host !== null && host !== socket) send(host, { type: "left", peer: state.peer });
  } else if (state.role === "host") {
    if (ctx.getWebSockets("host").some((other) => other !== socket)) return;
    for (const peer of ctx.getWebSockets("peer")) send(peer, { type: "host-status", online: false, relayOnly: state.relayOnly });
  }
}

/** Closes every peer socket past its lifetime; returns the earliest remaining close time. */
export function expirePeers(ctx: DurableObjectState, now: number): number | null {
  let next: number | null = null;
  for (const socket of ctx.getWebSockets("peer")) {
    const state = stateOf(socket);
    if (state?.closeAt == null) continue;
    if (now >= state.closeAt) close(socket, CLOSE.expired, "expired");
    else next = next === null ? state.closeAt : Math.min(next, state.closeAt);
  }
  return next;
}

/** Closes every socket in the room. */
export function closeRoom(ctx: DurableObjectState, code: number, reason: string): void {
  for (const socket of ctx.getWebSockets()) close(socket, code, reason);
}

function initialState(role: SignalRole, peer: string | null, relayOnly: boolean, self: string | null, closeAt: number | null, now: number): SocketState {
  return { role, peer, relayOnly, self, closeAt, windowStart: now, windowFrames: 0, invalidFrames: 0 };
}
