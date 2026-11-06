/**
 * Invite resolution and join tickets. Every miss looks the same: an unknown, malformed, revoked or expired code, a
 * code for a server that has gone, and a wrong code for an invite-only server all answer the identical 404.
 *
 * Misses count against the caller's address prefix, and the count is exact however many lookups run at once: each
 * lookup takes one of the prefix's miss slots before it looks anything up, in the same step as the check, and gives it
 * back unless it missed. A prefix out of slots is refused before any lookup.
 */
import { normalizeInviteCode } from "../shared/invite-code";
import { entropy } from "../shared/runtime";
import { JOIN_TICKET_TTL_SECONDS, inviteHash, issueJoinTicket } from "../shared/tokens";
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
  return lookup(context, async () => {
    const code = normalizeInviteCode(input);
    if (code === null) return null;
    const serverId = await context.inviteShard(code).resolve(code);
    // The shard is only an index: the code must still be one of the session's own live invites.
    const description = serverId === null ? null : await context.hostSession(serverId).describeInvite(serverId, code);
    if (serverId === null || description === null) return null;
    return jsonResponse(200, {
      serverId,
      listing: readListing(description.listing),
      descriptor: description.descriptor,
      descriptorSha256: description.descriptorSha256,
    });
  });
}

export async function createJoinTicket(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["serverId", "invite", "relayOnly"]);
  const serverId = expectString(body["serverId"], SERVER_ID_PATTERN);
  const relayOnly = expectBoolean(body["relayOnly"]);
  const inviteInput = body["invite"] === null ? null : expectString(body["invite"], undefined, 64);
  return lookup(context, async () => {
    await context.limit("join-ticket", TICKET_LIMITS);
    const code = inviteInput === null ? null : normalizeInviteCode(inviteInput);
    if (inviteInput !== null && code === null) return null;
    const admission = await context.hostSession(serverId).admit(serverId, code);
    if (!admission.ok) {
      if (admission.error === "not-found") return null;
      throw new ApiError(admission.error, admission.retryAfter);
    }
    const keys = await context.keys();
    const expiresAt = context.now + JOIN_TICKET_TTL_SECONDS;
    const ticket = await issueJoinTicket(keys.ticket, {
      serverId,
      epoch: admission.value.epoch,
      ticketId: toHex(entropy.bytes(16)),
      invite: code === null ? null : await inviteHash(keys.invite, code),
      expiresAt,
      relayOnly,
    });
    return jsonResponse(201, { ticket, serverId, expiresAt, relayOnly, hostRelayOnly: admission.value.hostRelayOnly });
  });
}

/**
 * Runs one lookup that can miss. It holds a miss slot while it runs, so a burst of wrong codes can never outrun the
 * limit, and keeps the slot only when the lookup missed (`work` gave null), which answers the uniform 404.
 */
async function lookup(context: RequestContext, work: () => Promise<Response | null>): Promise<Response> {
  await context.limit("invite-miss", MISS_LIMITS);
  let missed = false;
  try {
    const response = await work();
    if (response !== null) return response;
    missed = true;
    throw new ApiError("not-found");
  } finally {
    // A failed refund only leaves the prefix's count higher than it should be, which is the safe side.
    if (!missed) await context.refund("invite-miss", MISS_LIMITS).catch(() => undefined);
  }
}
