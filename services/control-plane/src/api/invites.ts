/**
 * Invite resolution and join tickets. Every miss looks the same: an unknown, malformed, revoked or expired code, a
 * code for a server that has gone, and a wrong code for an invite-only server all answer the identical 404. Misses
 * count against the caller's address prefix, and once a prefix has missed too often it is refused before any lookup.
 */
import { normalizeInviteCode } from "../shared/invite-code";
import { entropy } from "../shared/runtime";
import { JOIN_TICKET_TTL_SECONDS, issueJoinTicket } from "../shared/tokens";
import { toHex } from "../shared/wire-text";
import type { RequestContext } from "./context";
import { ApiError, jsonResponse, readJsonBody } from "./http";
import { readListing } from "./servers";
import { SERVER_ID_PATTERN, expectBoolean, expectObject, expectString } from "./validate";

const SMALL_BODY_BYTES = 1024;
export const MISS_LIMITS = [
  { window: "minute", limit: 10 },
  { window: "hour", limit: 30 },
] as const;
export const TICKET_LIMITS = [
  { window: "minute", limit: 20 },
  { window: "hour", limit: 120 },
] as const;

export async function resolveInvite(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["code"]);
  const input = expectString(body["code"], undefined, 64);
  await context.requireFeature("invites");
  await context.limit("invite-miss", MISS_LIMITS, "check");
  const code = normalizeInviteCode(input);
  const serverId = code === null ? null : await context.inviteShard(code).resolve(code);
  const description = serverId === null ? null : await context.hostSession(serverId).describe(serverId);
  if (serverId === null || description === null) return miss(context);
  return jsonResponse(200, {
    serverId,
    listing: readListing(description.listing),
    descriptor: description.descriptor,
    descriptorSha256: description.descriptorSha256,
  });
}

export async function createJoinTicket(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["serverId", "invite", "relayOnly"]);
  const serverId = expectString(body["serverId"], SERVER_ID_PATTERN);
  const relayOnly = expectBoolean(body["relayOnly"]);
  const inviteInput = body["invite"] === null ? null : expectString(body["invite"], undefined, 64);
  await context.limit("invite-miss", MISS_LIMITS, "check");
  await context.limit("join-ticket", TICKET_LIMITS);
  const code = inviteInput === null ? null : normalizeInviteCode(inviteInput);
  if (inviteInput !== null && code === null) return miss(context);
  const admission = await context.hostSession(serverId).admit(serverId, code);
  if (!admission.ok) {
    if (admission.error === "not-found") return miss(context);
    throw new ApiError(admission.error, admission.retryAfter);
  }
  const keys = await context.keys();
  const expiresAt = context.now + JOIN_TICKET_TTL_SECONDS;
  const ticket = await issueJoinTicket(keys.ticket, { serverId, ticketId: toHex(entropy.bytes(16)), expiresAt, relayOnly });
  return jsonResponse(201, { ticket, serverId, expiresAt, relayOnly, hostRelayOnly: admission.value.hostRelayOnly });
}

async function miss(context: RequestContext): Promise<Response> {
  await context.limit("invite-miss", MISS_LIMITS, "record");
  throw new ApiError("not-found");
}
