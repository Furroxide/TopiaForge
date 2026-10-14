/**
 * Keys derived from the single SERVICE_KEY secret with HKDF-SHA256, one per purpose, so rotating the secret rotates
 * them all and no key ever serves two purposes.
 *
 * - `challenge` authenticates host registration challenges.
 * - `ticket` authenticates join tickets.
 * - `rate` turns an address prefix and an hour into a rate-limit key. Keys change every hour, so a stored key never
 *   links one hour's traffic to the next, and without the secret it reveals nothing about the address.
 * - `self` fingerprints a signaling client's own address in memory, so a relay-only side's own address is recognized
 *   in a candidate without the address being kept.
 */
import { parseBase64Url, toHex, utf8, type Bytes } from "./wire-text";

export interface ServiceKeys {
  readonly challenge: CryptoKey;
  readonly ticket: CryptoKey;
  readonly rate: CryptoKey;
  readonly self: CryptoKey;
}

const SALT = utf8("TopiaForge control plane v1");
const MINIMUM_SECRET_BYTES = 32;
const MAXIMUM_SECRET_BYTES = 64;
const cache = new Map<string, Promise<ServiceKeys>>();

/** Whether `secret` is base64url of 32 to 64 bytes. */
export function isServiceKey(secret: unknown): secret is string {
  return decodeServiceKey(secret) !== null;
}

/** Derives the service keys, or null when SERVICE_KEY is missing or malformed. */
export function serviceKeys(secret: unknown): Promise<ServiceKeys> | null {
  const raw = decodeServiceKey(secret);
  if (raw === null) return null;
  const cached = cache.get(secret as string);
  if (cached !== undefined) return cached;
  const derived = derive(raw);
  cache.set(secret as string, derived);
  if (cache.size > 4) cache.delete(cache.keys().next().value as string);
  return derived;
}

export async function hmac(key: CryptoKey, ...parts: readonly Uint8Array[]): Promise<Bytes> {
  let length = 0;
  for (const part of parts) length += part.length;
  const message = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    message.set(part, offset);
    offset += part.length;
  }
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, message));
}

/** The first `bytes` bytes of an HMAC, as lowercase hexadecimal. */
export async function hmacHex(key: CryptoKey, text: string, bytes = 16): Promise<string> {
  return toHex((await hmac(key, utf8(text))).slice(0, bytes));
}

function decodeServiceKey(secret: unknown): Bytes | null {
  if (typeof secret !== "string") return null;
  for (let length = MINIMUM_SECRET_BYTES; length <= MAXIMUM_SECRET_BYTES; length++) {
    const raw = parseBase64Url(secret, length);
    if (raw !== null) return raw;
  }
  return null;
}

async function derive(raw: Bytes): Promise<ServiceKeys> {
  const master = await crypto.subtle.importKey("raw", raw, "HKDF", false, ["deriveBits"]);
  const one = async (label: string): Promise<CryptoKey> => {
    const bits = await crypto.subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt: SALT, info: utf8(label) }, master, 256);
    return crypto.subtle.importKey("raw", bits, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  };
  const [challenge, ticket, rate, self] = await Promise.all([one("challenge"), one("ticket"), one("rate"), one("self")]);
  return { challenge, ticket, rate, self };
}
