/**
 * The three credentials the control plane issues.
 *
 * - A **host challenge** is stateless: version, expiry, a random nonce and a MAC that binds them to the signing key
 *   it was issued for. The host session records each nonce it accepts until expiry, so a challenge works once.
 * - A **host session token** is 32 random bytes. The host session keeps only its SHA-256.
 * - A **join ticket** is stateless: version, server id, the host session's epoch, ticket id, a keyed hash of the
 *   invite it was issued under, expiry and flags under a MAC. Tickets open signaling and mint relay credentials, and
 *   the host session honours one only for its own epoch and while its invite is still live; the spend guard counts
 *   per ticket id.
 *
 * MACs are truncated to 16 bytes, compared in constant time, and keyed per purpose (see keys.ts).
 */
import { hmac, hmacHex } from "./keys";
import {
  bytesEqual,
  concatBytes,
  parseBase64Url,
  parseHex,
  readUint,
  toBase64Url,
  toHex,
  uintBytes,
  utf8,
  type Bytes,
} from "./wire-text";

export const CHALLENGE_TTL_SECONDS = 120;
export const JOIN_TICKET_TTL_SECONDS = 6 * 60 * 60;
export const HOST_TOKEN_PREFIX = "tfh1_";
export const JOIN_TICKET_PREFIX = "tfj1_";

const CHALLENGE_LABEL = utf8("TopiaForge host challenge v1\n");
const TICKET_LABEL = utf8("TopiaForge join ticket v1\n");
const VERSION = 1;
const MAC_BYTES = 16;
const NONCE_BYTES = 16;
const CHALLENGE_BYTES = 1 + 8 + NONCE_BYTES + MAC_BYTES;
const TICKET_ID_BYTES = 16;
export const SESSION_EPOCH_BYTES = 16;
const INVITE_HASH_BYTES = 16;
const RELAY_ONLY_FLAG = 1;
const INVITE_FLAG = 2;
const EPOCH_OFFSET = 1 + 32;
const TICKET_ID_OFFSET = EPOCH_OFFSET + SESSION_EPOCH_BYTES;
const INVITE_OFFSET = TICKET_ID_OFFSET + TICKET_ID_BYTES;
const EXPIRY_OFFSET = INVITE_OFFSET + INVITE_HASH_BYTES;
const TICKET_PAYLOAD_BYTES = EXPIRY_OFFSET + 8 + 1;
const TICKET_BYTES = TICKET_PAYLOAD_BYTES + MAC_BYTES;
const HOST_TOKEN_BYTES = 32;

export interface Challenge {
  readonly bytes: Bytes;
  readonly nonce: string;
  readonly expiresAt: number;
}

export interface JoinTicket {
  readonly serverId: string;
  /** The host session it was issued for. A host that ends its session and registers again starts a new epoch. */
  readonly epoch: string;
  readonly ticketId: string;
  /** The keyed hash of the invite it was issued under (`inviteHash`), or null when it was issued without one. */
  readonly invite: string | null;
  readonly expiresAt: number;
  readonly relayOnly: boolean;
}

export async function issueChallenge(key: CryptoKey, signingKey: Bytes, nonce: Bytes, now: number): Promise<string> {
  const head = concatBytes(Uint8Array.of(VERSION), uintBytes(now + CHALLENGE_TTL_SECONDS, 8), nonce.slice(0, NONCE_BYTES));
  const mac = (await hmac(key, CHALLENGE_LABEL, signingKey, head)).slice(0, MAC_BYTES);
  return toBase64Url(concatBytes(head, mac));
}

/** Reads a challenge issued for `signingKey` that has not expired, or null. */
export async function readChallenge(key: CryptoKey, text: unknown, signingKey: Bytes, now: number): Promise<Challenge | null> {
  const bytes = decodeBase64UrlOfLength(text, CHALLENGE_BYTES);
  if (bytes === null || bytes[0] !== VERSION) return null;
  const head = bytes.slice(0, CHALLENGE_BYTES - MAC_BYTES);
  const expected = (await hmac(key, CHALLENGE_LABEL, signingKey, head)).slice(0, MAC_BYTES);
  if (!bytesEqual(expected, bytes.slice(CHALLENGE_BYTES - MAC_BYTES))) return null;
  const expiresAt = readUint(bytes, 1, 8);
  if (expiresAt <= now || expiresAt > now + CHALLENGE_TTL_SECONDS) return null;
  return { bytes, nonce: toHex(bytes.slice(9, 9 + NONCE_BYTES)), expiresAt };
}

export function newHostToken(random: Bytes): string {
  if (random.length < HOST_TOKEN_BYTES) throw new Error("A host token needs 32 random bytes.");
  return HOST_TOKEN_PREFIX + toBase64Url(random.slice(0, HOST_TOKEN_BYTES));
}

/** The SHA-256 of a well-formed host token, as hexadecimal, or null for anything else. */
export async function hostTokenHash(token: unknown): Promise<string | null> {
  if (typeof token !== "string" || !token.startsWith(HOST_TOKEN_PREFIX)) return null;
  const raw = decodeBase64UrlOfLength(token.slice(HOST_TOKEN_PREFIX.length), HOST_TOKEN_BYTES);
  if (raw === null) return null;
  return toHex(new Uint8Array(await crypto.subtle.digest("SHA-256", utf8(token))));
}

/** The keyed hash a join ticket carries for the canonical invite code it was issued under, as hexadecimal. */
export function inviteHash(key: CryptoKey, code: string): Promise<string> {
  return hmacHex(key, `invite v1|${code}`, INVITE_HASH_BYTES);
}

export async function issueJoinTicket(key: CryptoKey, ticket: JoinTicket): Promise<string> {
  const serverId = parseHex(ticket.serverId, 32);
  const epoch = parseHex(ticket.epoch, SESSION_EPOCH_BYTES);
  const ticketId = parseHex(ticket.ticketId, TICKET_ID_BYTES);
  const invite = ticket.invite === null ? new Uint8Array(INVITE_HASH_BYTES) : parseHex(ticket.invite, INVITE_HASH_BYTES);
  if (serverId === null || epoch === null || ticketId === null || invite === null) {
    throw new Error("A join ticket needs a server id, a session epoch, a ticket id and, when it has one, an invite hash.");
  }
  const flags = (ticket.relayOnly ? RELAY_ONLY_FLAG : 0) | (ticket.invite === null ? 0 : INVITE_FLAG);
  const payload = concatBytes(Uint8Array.of(VERSION), serverId, epoch, ticketId, invite, uintBytes(ticket.expiresAt, 8), Uint8Array.of(flags));
  const mac = (await hmac(key, TICKET_LABEL, payload)).slice(0, MAC_BYTES);
  return JOIN_TICKET_PREFIX + toBase64Url(concatBytes(payload, mac));
}

/** Reads an authentic, unexpired join ticket, or null. */
export async function readJoinTicket(key: CryptoKey, text: unknown, now: number): Promise<JoinTicket | null> {
  if (typeof text !== "string" || !text.startsWith(JOIN_TICKET_PREFIX)) return null;
  const bytes = decodeBase64UrlOfLength(text.slice(JOIN_TICKET_PREFIX.length), TICKET_BYTES);
  if (bytes === null || bytes[0] !== VERSION) return null;
  const payload = bytes.slice(0, TICKET_PAYLOAD_BYTES);
  const expected = (await hmac(key, TICKET_LABEL, payload)).slice(0, MAC_BYTES);
  if (!bytesEqual(expected, bytes.slice(TICKET_PAYLOAD_BYTES))) return null;
  const flags = payload[TICKET_PAYLOAD_BYTES - 1]!;
  const expiresAt = readUint(payload, EXPIRY_OFFSET, 8);
  const invite = payload.slice(INVITE_OFFSET, EXPIRY_OFFSET);
  const hasInvite = (flags & INVITE_FLAG) !== 0;
  if (flags > (RELAY_ONLY_FLAG | INVITE_FLAG) || expiresAt <= now) return null;
  // A ticket issued without an invite carries none: one spelling per ticket.
  if (!hasInvite && invite.some((value) => value !== 0)) return null;
  return {
    serverId: toHex(payload.slice(1, EPOCH_OFFSET)),
    epoch: toHex(payload.slice(EPOCH_OFFSET, TICKET_ID_OFFSET)),
    ticketId: toHex(payload.slice(TICKET_ID_OFFSET, INVITE_OFFSET)),
    invite: hasInvite ? toHex(invite) : null,
    expiresAt,
    relayOnly: (flags & RELAY_ONLY_FLAG) !== 0,
  };
}

/** Pulls the bearer credential from an Authorization header, or null. */
export function bearer(header: string | null): string | null {
  if (header === null || !header.startsWith("Bearer ")) return null;
  const value = header.slice(7);
  return value.length > 0 && value.length <= 256 && /^[A-Za-z0-9_-]+$/.test(value) ? value : null;
}

function decodeBase64UrlOfLength(text: unknown, byteCount: number): Bytes | null {
  return parseBase64Url(text, byteCount);
}
