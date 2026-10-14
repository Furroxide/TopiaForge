/**
 * The signaling room inside a host session. Sockets use the hibernation API: the runtime keeps each socket's small
 * attachment in memory, and no signaling frame is ever written to storage. The host's socket lives as long as the
 * registration it was opened under; a peer's lives at most PEER_SOCKET_SECONDS.
 *
 * Privacy is enforced here, not trusted to clients: a relay-only side's frames reach the other side only after
 * `permittedCandidates` has removed every candidate that could reveal its address.
 *
 * A socket the service has closed stays listed by the runtime until its client finishes the close, so every count and
 * every route here looks only at open sockets, and a frame that still arrives on a closing socket goes nowhere.
 */
import { stringify, type JsonObject } from "../shared/canonical-json";
import {
  candidatesOf,
  forwardedFrame,
  permittedCandidates,
  readSignal,
  MAXIMUM_FRAME_BYTES,
  type CandidatePolicy,
  type SignalRole,
} from "../shared/signaling";

export const PEER_SOCKET_SECONDS = 15 * 60;
/** A room has a place for every joiner a full session holds (maxPlayers - 1), and three more for reconnects. */
const SPARE_PLACES = 3;
/** The most places one /24 or /56 may hold in a room; it never holds more than half of them either. */
const MAXIMUM_PLACES_PER_SOURCE = 4;
const RATE_WINDOW_SECONDS = 10;
const RATE_WINDOW_FRAMES = 40;
const MAXIMUM_INVALID_FRAMES = 5;

export const CLOSE = {
  normal: 1000,
  unsupported: 1003,
  tooLarge: 1009,
  invalid: 4400,
  withdrawn: 4403,
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
  /** The keyed fingerprint of the side's own address scope, or null when the service saw no address. */
  readonly self: string | null;
  /** The host's session token hash, which tells a socket from an earlier registration apart; null for a peer. */
  readonly tokenHash: string | null;
  /** A peer's keyed /24 or /56 fingerprint, which the room's per-source cap counts; null for the host. */
  readonly source: string | null;
  /** The keyed hash of the invite a peer's ticket was issued with; null for the host and for a ticket without one. */
  readonly invite: string | null;
  /** When a peer's ticket expires, which bounds how long a host's goodbye keeps it out; null for the host. */
  readonly ticketExpiresAt: number | null;
  readonly closeAt: number | null;
  readonly windowStart: number;
  readonly windowFrames: number;
  readonly invalidFrames: number;
}

/** What the host session established about a host's upgrade. */
export interface HostAdmission {
  readonly now: number;
  readonly tokenHash: string;
  readonly relayOnly: boolean;
  readonly self: string | null;
}

/** What the host session established about a peer's upgrade. */
export interface PeerAdmission {
  readonly now: number;
  readonly peer: string;
  readonly relayOnly: boolean;
  readonly self: string | null;
  readonly source: string;
  readonly invite: string | null;
  readonly ticketExpiresAt: number;
  readonly hostRelayOnly: boolean;
  readonly maxPlayers: number;
}

export interface RoomContext {
  readonly ctx: DurableObjectState;
  readonly now: number;
  /** Whether the session is live; a lapsed session's sockets close on their next frame. */
  readonly sessionLive: boolean;
  readonly policy: CandidatePolicy;
  /**
   * Keeps a peer the host said goodbye to out of the room until its ticket expires: the expiry a socket the room still
   * lists for it carries, or, with null, the one its admission recorded. A peer id the room never admitted stays
   * unrecorded.
   */
  readonly exclude: (peer: string, listedExpiry: number | null) => void;
}

export function stateOf(socket: WebSocket): SocketState | null {
  return socket.deserializeAttachment() as SocketState | null;
}

export function isOpen(socket: WebSocket): boolean {
  return socket.readyState === WebSocket.READY_STATE_OPEN;
}

export function openSockets(ctx: DurableObjectState, tag: string): WebSocket[] {
  return ctx.getWebSockets(tag).filter(isOpen);
}

export function hostSocket(ctx: DurableObjectState): WebSocket | null {
  return openSockets(ctx, "host")[0] ?? null;
}

export function peerSocket(ctx: DurableObjectState, peer: string): WebSocket | null {
  return openSockets(ctx, `peer:${peer}`)[0] ?? null;
}

/** How many peer sockets a room holds for a session of `maxPlayers`. */
export function roomCapacity(maxPlayers: number): number {
  return maxPlayers - 1 + SPARE_PLACES;
}

/** How many of a room's places one /24 or /56 may hold. */
export function perSourceLimit(capacity: number): number {
  return Math.min(MAXIMUM_PLACES_PER_SOURCE, Math.floor(capacity / 2));
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
export function acceptHost(ctx: DurableObjectState, host: HostAdmission): Response {
  for (const old of ctx.getWebSockets("host")) close(old, CLOSE.replaced, "replaced");
  const [client, server] = Object.values(new WebSocketPair()) as [WebSocket, WebSocket];
  ctx.acceptWebSocket(server, ["host"]);
  server.serializeAttachment(initialState("host", host.now, { relayOnly: host.relayOnly, self: host.self, tokenHash: host.tokenHash }));
  const peers = openSockets(ctx, "peer")
    .map(stateOf)
    .filter((state): state is SocketState => state !== null && state.peer !== null)
    .map((state) => ({ peer: state.peer!, relayOnly: state.relayOnly }));
  send(server, { type: "welcome", role: "host", peers });
  for (const peer of openSockets(ctx, "peer")) send(peer, { type: "host-status", online: true, relayOnly: host.relayOnly });
  return new Response(null, { status: 101, webSocket: client });
}

/**
 * Accepts a peer's socket, replacing an earlier one for the same ticket. Null when the room is full, or when the
 * peer's /24 or /56 already holds its share of the room, so no one network can take every place.
 */
export function acceptPeer(ctx: DurableObjectState, admission: PeerAdmission): Response | null {
  const capacity = roomCapacity(admission.maxPlayers);
  const others = openSockets(ctx, "peer").filter((socket) => stateOf(socket)?.peer !== admission.peer);
  if (others.length >= capacity) return null;
  if (others.filter((socket) => stateOf(socket)?.source === admission.source).length >= perSourceLimit(capacity)) return null;
  for (const old of ctx.getWebSockets(`peer:${admission.peer}`)) close(old, CLOSE.replaced, "replaced");
  const [client, server] = Object.values(new WebSocketPair()) as [WebSocket, WebSocket];
  ctx.acceptWebSocket(server, ["peer", `peer:${admission.peer}`]);
  server.serializeAttachment(
    initialState("peer", admission.now, {
      peer: admission.peer,
      relayOnly: admission.relayOnly,
      self: admission.self,
      source: admission.source,
      invite: admission.invite,
      ticketExpiresAt: admission.ticketExpiresAt,
      closeAt: Math.min(admission.now + PEER_SOCKET_SECONDS, admission.ticketExpiresAt),
    }),
  );
  const host = hostSocket(ctx);
  send(server, { type: "welcome", role: "peer", peer: admission.peer, hostOnline: host !== null, hostRelayOnly: admission.hostRelayOnly });
  if (host !== null) send(host, { type: "joined", peer: admission.peer, relayOnly: admission.relayOnly });
  return new Response(null, { status: 101, webSocket: client });
}

/** Closes every host socket opened under another registration than the one with `tokenHash`. */
export function closeStaleHosts(ctx: DurableObjectState, tokenHash: string): void {
  for (const socket of ctx.getWebSockets("host")) {
    if (stateOf(socket)?.tokenHash !== tokenHash) close(socket, CLOSE.replaced, "replaced");
  }
}

/** Closes every open peer socket whose state `keep` refuses. */
export function closePeersUnless(ctx: DurableObjectState, keep: (state: SocketState) => boolean, code: number, reason: string): void {
  for (const socket of openSockets(ctx, "peer")) {
    const state = stateOf(socket);
    if (state === null || !keep(state)) close(socket, code, reason);
  }
}

/** Handles one frame from either side. */
export async function onFrame(room: RoomContext, socket: WebSocket, frame: string | ArrayBuffer): Promise<void> {
  if (!isOpen(socket)) return;
  const state = stateOf(socket);
  if (state === null) return close(socket, CLOSE.invalid, "unknown");
  if (!room.sessionLive) return close(socket, CLOSE.sessionEnded, "session ended");
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

  // The host's goodbye keeps that ticket out of the room for as long as it could open it, whether or not the peer is
  // still connected: a peer whose socket has already closed cannot come back with the same ticket either.
  const farewell = state.role === "host" && message.type === "bye";
  if (farewell) room.exclude(message.peer!, listedTicketExpiry(room.ctx, message.peer!));
  const target = state.role === "peer" ? hostSocket(room.ctx) : peerSocket(room.ctx, message.peer!);
  if (target === null) {
    // A goodbye has done what it asks without being delivered; any other frame for a missing counterpart is refused.
    if (!farewell) send(socket, { type: "error", code: state.role === "peer" ? "host-offline" : "unknown-peer" });
    return;
  }
  const offered = candidatesOf(message);
  // Only candidates need the filter's hashing; a punch or a goodbye is handled in the same turn as the checks above.
  const kept = offered.length === 0 ? offered : await permittedCandidates(offered, { relayOnly: state.relayOnly, ownScope: state.self }, room.policy);
  if (kept.length < offered.length) send(socket, { type: "notice", code: "candidates-withheld", withheld: offered.length - kept.length });
  // A trickled candidate that may not leave is dropped whole; every other frame goes on with what may.
  if (message.type !== "candidate" || kept.length === 1) {
    send(target, forwardedFrame(message, state.role === "peer" ? state.peer : null, kept));
  }
  if (message.type !== "bye") return;
  if (state.role === "peer") return close(socket, CLOSE.normal, "bye");
  close(target, CLOSE.normal, "bye");
}

/** The ticket expiry that every socket the room still lists for `peer` carries; null once none is left. */
function listedTicketExpiry(ctx: DurableObjectState, peer: string): number | null {
  for (const socket of ctx.getWebSockets(`peer:${peer}`)) {
    const expiresAt = stateOf(socket)?.ticketExpiresAt;
    if (expiresAt !== null && expiresAt !== undefined) return expiresAt;
  }
  return null;
}

/** Tells the other side that a socket went away. */
export function onClosed(ctx: DurableObjectState, socket: WebSocket): void {
  const state = stateOf(socket);
  if (state === null) return;
  if (state.role === "peer" && state.peer !== null) {
    // A replacement socket for the same ticket keeps the peer present.
    if (openSockets(ctx, `peer:${state.peer}`).some((other) => other !== socket)) return;
    const host = hostSocket(ctx);
    if (host !== null && host !== socket) send(host, { type: "left", peer: state.peer });
  } else if (state.role === "host") {
    if (openSockets(ctx, "host").some((other) => other !== socket)) return;
    for (const peer of openSockets(ctx, "peer")) send(peer, { type: "host-status", online: false, relayOnly: state.relayOnly });
  }
}

/** Closes every peer socket past its lifetime; returns the earliest remaining close time. */
export function expirePeers(ctx: DurableObjectState, now: number): number | null {
  let next: number | null = null;
  for (const socket of openSockets(ctx, "peer")) {
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

function initialState(role: SignalRole, now: number, fields: Partial<SocketState> & Pick<SocketState, "relayOnly" | "self">): SocketState {
  return {
    role,
    peer: null,
    tokenHash: null,
    source: null,
    invite: null,
    ticketExpiresAt: null,
    closeAt: null,
    ...fields,
    windowStart: now,
    windowFrames: 0,
    invalidFrames: 0,
  };
}
