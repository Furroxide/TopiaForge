/**
 * Host endpoints. A host proves it holds its server's signing key by signing a fresh challenge together with the
 * exact registration it sends; everything after that uses the session token registration returns.
 */
import { stringify, type JsonObject, type JsonValue } from "../shared/canonical-json";
import { MAXIMUM_DESCRIPTOR_BYTES, verifyEd25519, verifySignedDescriptor, type SignedDescriptor } from "../shared/descriptor";
import { displayInviteCode, inviteLink, isCanonicalInviteCode } from "../shared/invite-code";
import { summarize } from "../shared/listing";
import { entropy } from "../shared/runtime";
import { CHALLENGE_TTL_SECONDS, bearer, hostTokenHash, issueChallenge, newHostToken, readChallenge } from "../shared/tokens";
import { concatBytes, parseBase64Url, utf8 } from "../shared/wire-text";
import type { InviteRow } from "../objects/host-store";
import { HEARTBEAT_SECONDS, LISTING_TTL_SECONDS, MAXIMUM_INVITE_SECONDS, MINIMUM_INVITE_SECONDS, type Outcome } from "../objects/host-session";
import type { RequestContext } from "./context";
import { ApiError, emptyResponse, jsonResponse, readJsonBody, requireNoBody } from "./http";
import { expectBoolean, expectInteger, expectObject, expectString } from "./validate";

const REGISTRATION_LABEL = utf8("TopiaForge host registration v1\n");
const SMALL_BODY_BYTES = 1024;
/** A registration carries the signed descriptor as a JSON string, so allow for its escaping. */
const REGISTRATION_BODY_BYTES = 2 * MAXIMUM_DESCRIPTOR_BYTES + 1024;

export const CHALLENGE_LIMITS = [
  { window: "minute", limit: 10 },
  { window: "hour", limit: 60 },
] as const;
export const REGISTRATION_LIMITS = [
  { window: "minute", limit: 6 },
  { window: "hour", limit: 30 },
] as const;

export async function challenge(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["signingKey"]);
  const signingKey = parseBase64Url(body["signingKey"], 32);
  if (signingKey === null) throw new ApiError("invalid-request");
  await context.requireFeature("hosting");
  await context.limit("challenge", CHALLENGE_LIMITS);
  const keys = await context.keys();
  const issued = await issueChallenge(keys.challenge, signingKey, entropy.bytes(16), context.now);
  return jsonResponse(200, { challenge: issued, expiresAt: context.now + CHALLENGE_TTL_SECONDS });
}

export async function register(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, REGISTRATION_BODY_BYTES), ["challenge", "descriptor", "proof", "relayOnly"]);
  const relayOnly = expectBoolean(body["relayOnly"]);
  const proof = parseBase64Url(body["proof"], 64);
  if (proof === null || typeof body["challenge"] !== "string") throw new ApiError("invalid-request");
  await context.requireFeature("hosting");
  await context.limit("register", REGISTRATION_LIMITS);
  const signed = await readDescriptor(body["descriptor"], null);
  const keys = await context.keys();
  const challengeRecord = await readChallenge(keys.challenge, body["challenge"], signed.descriptor.signingKey, context.now);
  if (challengeRecord === null) throw new ApiError("challenge-invalid");
  const unsigned = utf8(stringify({ challenge: body["challenge"], descriptor: body["descriptor"]!, relayOnly }));
  if (!(await verifyEd25519(signed.descriptor.signingKey, proof, concatBytes(REGISTRATION_LABEL, unsigned)))) {
    throw new ApiError("proof-invalid");
  }

  const token = newHostToken(entropy.bytes(32));
  const tokenHash = (await hostTokenHash(token))!;
  const descriptor = signed.descriptor;
  const outcome = await context.hostSession(descriptor.serverId).register({
    serverId: descriptor.serverId,
    descriptor: body["descriptor"] as string,
    descriptorSha256: signed.sha256,
    issuedAt: descriptor.issuedAt,
    visibility: descriptor.visibility === "invite" ? "invite" : "public",
    summary: summarize(descriptor),
    tokenHash,
    challengeNonce: challengeRecord.nonce,
    challengeExpiresAt: challengeRecord.expiresAt,
    relayOnly,
  });
  const value = unwrap(outcome);
  return jsonResponse(201, {
    serverId: descriptor.serverId,
    sessionToken: token,
    descriptorSha256: signed.sha256,
    expiresAt: value.expiresAt,
    heartbeatSeconds: HEARTBEAT_SECONDS,
    listingTtlSeconds: LISTING_TTL_SECONDS,
    listed: value.listed,
    invite: inviteJson(value.invite),
  });
}

export async function hostView(context: RequestContext, serverId: string): Promise<Response> {
  requireNoBody(context.request);
  const tokenHash = await hostToken(context);
  const view = unwrap(await context.hostSession(serverId).view(serverId, tokenHash));
  return jsonResponse(200, {
    serverId,
    visibility: view.visibility,
    players: view.players,
    relayOnly: view.relayOnly,
    descriptorSha256: view.descriptorSha256,
    expiresAt: view.expiresAt,
    listed: view.listed,
    invites: view.invites.map(inviteJson),
  });
}

export async function heartbeat(context: RequestContext, serverId: string): Promise<Response> {
  const tokenHash = await hostToken(context);
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["players"]);
  const players = expectInteger(body["players"], 0, 16);
  const status = unwrap(await context.hostSession(serverId).heartbeat(serverId, tokenHash, players));
  return jsonResponse(200, { expiresAt: status.expiresAt, listed: status.listed, heartbeatSeconds: HEARTBEAT_SECONDS });
}

export async function updateDescriptor(context: RequestContext, serverId: string): Promise<Response> {
  const tokenHash = await hostToken(context);
  const body = expectObject(await readJsonBody(context.request, REGISTRATION_BODY_BYTES), ["descriptor"]);
  await context.limit("descriptor", REGISTRATION_LIMITS);
  const signed = await readDescriptor(body["descriptor"], serverId);
  const status = unwrap(
    await context.hostSession(serverId).updateDescriptor(serverId, tokenHash, {
      descriptor: body["descriptor"] as string,
      descriptorSha256: signed.sha256,
      issuedAt: signed.descriptor.issuedAt,
      visibility: signed.descriptor.visibility === "invite" ? "invite" : "public",
      summary: summarize(signed.descriptor),
    }),
  );
  return jsonResponse(200, { descriptorSha256: signed.sha256, expiresAt: status.expiresAt, listed: status.listed });
}

export async function endHost(context: RequestContext, serverId: string): Promise<Response> {
  requireNoBody(context.request);
  const tokenHash = await hostToken(context);
  unwrap(await context.hostSession(serverId).end(serverId, tokenHash));
  return emptyResponse();
}

export async function createInvite(context: RequestContext, serverId: string): Promise<Response> {
  const tokenHash = await hostToken(context);
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["ttlSeconds"]);
  const seconds = expectInteger(body["ttlSeconds"], MINIMUM_INVITE_SECONDS, MAXIMUM_INVITE_SECONDS);
  await context.requireFeature("invites");
  const invite = unwrap(await context.hostSession(serverId).createInvite(serverId, tokenHash, seconds));
  return jsonResponse(201, { invite: inviteJson(invite) });
}

/** Withdraws one of the session's invites. The code travels in the body, so it never appears in a URL. */
export async function revokeInvite(context: RequestContext, serverId: string): Promise<Response> {
  const tokenHash = await hostToken(context);
  const body = expectObject(await readJsonBody(context.request, SMALL_BODY_BYTES), ["code"]);
  const code = expectString(body["code"], undefined, 64);
  if (!isCanonicalInviteCode(code)) throw new ApiError("not-found");
  unwrap(await context.hostSession(serverId).revokeInvite(serverId, tokenHash, code));
  return emptyResponse();
}

export function inviteJson(invite: InviteRow): JsonObject {
  return { code: invite.code, display: displayInviteCode(invite.code), link: inviteLink(invite.code), expiresAt: invite.expiresAt };
}

export function unwrap<T>(outcome: Outcome<T>): T {
  if (!outcome.ok) throw new ApiError(outcome.error, outcome.retryAfter);
  return outcome.value;
}

async function hostToken(context: RequestContext): Promise<string> {
  const hash = await hostTokenHash(bearer(context.request.headers.get("authorization")));
  if (hash === null) throw new ApiError("unauthorized");
  return hash;
}

/** Verifies a signed descriptor sent as a JSON string, refusing LAN-only sessions. */
async function readDescriptor(value: JsonValue | undefined, expectedServerId: string | null): Promise<SignedDescriptor> {
  const text = expectString(value, undefined, MAXIMUM_DESCRIPTOR_BYTES);
  const verified = await verifySignedDescriptor(utf8(text), expectedServerId);
  if (!verified.ok) throw new ApiError("invalid-descriptor");
  if (verified.signed.descriptor.visibility === "lan") throw new ApiError("unsupported-visibility");
  return verified.signed;
}
