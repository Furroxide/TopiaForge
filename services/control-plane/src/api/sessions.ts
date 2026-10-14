/**
 * Signaling upgrades and relay credentials. Both are open to either side of a session: the host with its session
 * token, a joining player with a join ticket for that server.
 */
import type { JoinTicket } from "../shared/tokens";
import { JOIN_TICKET_PREFIX, JOIN_TICKET_TTL_SECONDS, bearer, hostTokenHash, readJoinTicket } from "../shared/tokens";
import type { RequestContext } from "./context";
import { unwrap } from "./hosts";
import { ApiError, jsonResponse, readJsonBody } from "./http";
import { SERVER_ID_PATTERN, expectObject, expectString } from "./validate";

export const SIGNAL_LIMITS = [
  { window: "minute", limit: 30 },
  { window: "hour", limit: 240 },
] as const;

type Side = { readonly kind: "host"; readonly tokenHash: string } | { readonly kind: "join"; readonly ticket: JoinTicket };

export async function openSignaling(context: RequestContext, serverId: string): Promise<Response> {
  if (context.request.headers.get("upgrade")?.toLowerCase() !== "websocket") throw new ApiError("invalid-request");
  await context.requireFeature("signaling");
  await context.limit("signal", SIGNAL_LIMITS);
  const side = await authenticate(context, serverId);
  // A fresh request carries only what the session needs; nothing the client sent passes through unchecked.
  const headers = new Headers({ upgrade: "websocket", "x-signal-server": serverId });
  const self = await context.selfFingerprint();
  if (self !== null) headers.set("x-signal-self", self);
  if (side.kind === "host") {
    headers.set("x-signal-role", "host");
    headers.set("x-signal-token-hash", side.tokenHash);
  } else {
    headers.set("x-signal-role", "peer");
    headers.set("x-signal-peer", side.ticket.ticketId.slice(0, 16));
    headers.set("x-signal-relay-only", side.ticket.relayOnly ? "1" : "0");
    headers.set("x-signal-ticket-expires", String(side.ticket.expiresAt));
  }
  const response = await context.hostSession(serverId).fetch(new Request("https://host-session.internal/signal", { headers }));
  if (response.status === 101) return response;
  await response.body?.cancel();
  throw new ApiError(response.status === 401 ? "unauthorized" : response.status === 409 ? "room-full" : "not-found");
}

export async function mintRelayCredentials(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, 1024), ["serverId"]);
  const serverId = expectString(body["serverId"], SERVER_ID_PATTERN);
  const flags = await context.flags();
  if (!flags.features.relay || flags.killSwitch.engaged || context.config.provider === null) throw new ApiError("relay-unavailable");
  const side = await authenticate(context, serverId);
  const grant = unwrap(await context.hostSession(serverId).relayGrant(serverId, side.kind === "host" ? side.tokenHash : null));
  const { keys } = await context.bucket();
  const outcome = await context.spendGuard().mint({
    ticket: side.kind === "host" ? `host:${side.tokenHash.slice(0, 32)}` : `join:${side.ticket.ticketId}`,
    ticketExpiresAt: side.kind === "host" ? context.now + JOIN_TICKET_TTL_SECONDS : side.ticket.expiresAt,
    peers: grant.peers,
    keys,
    at: context.at,
  });
  if (!outcome.ok) throw new ApiError(outcome.error, outcome.retryAfter);
  return jsonResponse(201, {
    iceServers: outcome.iceServers.map((server) => ({ ...server, urls: [...server.urls] })),
    expiresAt: outcome.expiresAt,
    ttlSeconds: context.config.limits.credentialTtlSeconds,
  });
}

/** The host by its session token, or a joining player by a join ticket issued for this server. */
async function authenticate(context: RequestContext, serverId: string): Promise<Side> {
  const credential = bearer(context.request.headers.get("authorization"));
  if (credential !== null && credential.startsWith(JOIN_TICKET_PREFIX)) {
    const ticket = await readJoinTicket((await context.keys()).ticket, credential, context.now);
    if (ticket === null || ticket.serverId !== serverId) throw new ApiError("unauthorized");
    return { kind: "join", ticket };
  }
  const tokenHash = await hostTokenHash(credential);
  if (tokenHash === null) throw new ApiError("unauthorized");
  return { kind: "host", tokenHash };
}
