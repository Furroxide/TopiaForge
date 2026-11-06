/**
 * Invite codes and the join links the control plane deals in (docs/internal/MultiplayerLiveSessions.md, section 4).
 *
 * A code is 10 characters of Crockford base32, 50 random bits. Reading is forgiving: ASCII case, hyphens and spaces
 * are ignored, O reads as 0, and I and L read as 1. The canonical spelling is upper case without separators; people
 * see it as XXXXX-XXXXX. Folding is ASCII-only, so no other script's letters ever stand in for a code character.
 *
 * The service formats `topiaforge://join/<code>` links and reads invite and server links. Direct links carry a host
 * address, so they never pass through the service at all.
 */
import { isHex } from "./wire-text";

export const INVITE_CODE_LENGTH = 10;
export const INVITE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const MAXIMUM_INPUT_LENGTH = 32;
const LINK_SCHEME = "topiaforge";
const LINK_PREFIX = "topiaforge://join/";
const MAXIMUM_LINK_LENGTH = 512;

export function normalizeInviteCode(input: unknown): string | null {
  if (typeof input !== "string" || input.length > MAXIMUM_INPUT_LENGTH) return null;
  let code = "";
  for (const raw of input) {
    if (raw === "-" || raw === " ") continue;
    let character = raw >= "a" && raw <= "z" ? String.fromCharCode(raw.charCodeAt(0) - 32) : raw;
    if (character === "O") character = "0";
    else if (character === "I" || character === "L") character = "1";
    if (!INVITE_ALPHABET.includes(character) || code.length === INVITE_CODE_LENGTH) return null;
    code += character;
  }
  return code.length === INVITE_CODE_LENGTH ? code : null;
}

export function isCanonicalInviteCode(code: unknown): code is string {
  return typeof code === "string" && normalizeInviteCode(code) === code;
}

/** Formats a canonical code for people: two groups of five. */
export function displayInviteCode(code: string): string {
  if (!isCanonicalInviteCode(code)) throw new Error("A canonical invite code is required.");
  return `${code.slice(0, 5)}-${code.slice(5)}`;
}

export function inviteLink(code: string): string {
  if (!isCanonicalInviteCode(code)) throw new Error("A canonical invite code is required.");
  return LINK_PREFIX + code;
}

/** A fresh code from 50 uniformly random bits: each byte's low five bits pick one character. */
export function inviteCodeFrom(random: Uint8Array): string {
  if (random.length < INVITE_CODE_LENGTH) throw new Error("An invite code needs ten random bytes.");
  let code = "";
  for (let index = 0; index < INVITE_CODE_LENGTH; index++) code += INVITE_ALPHABET[random[index]! & 0x1f]!;
  return code;
}

export type JoinLink =
  | { readonly kind: "invite"; readonly inviteCode: string; readonly canonical: string }
  | { readonly kind: "server"; readonly serverId: string; readonly canonical: string };

/**
 * Reads an invite or server link under the shared grammar: at most 512 characters, no query, fragment or percent
 * encoding, and only the scheme ignores case. Direct links and anything malformed give null.
 */
export function parseJoinLink(text: unknown): JoinLink | null {
  if (typeof text !== "string" || text.length > MAXIMUM_LINK_LENGTH || text.length <= LINK_PREFIX.length) return null;
  if (text.slice(0, LINK_SCHEME.length).toLowerCase() !== LINK_SCHEME) return null;
  if (text.slice(LINK_SCHEME.length, LINK_PREFIX.length) !== LINK_PREFIX.slice(LINK_SCHEME.length)) return null;
  for (const character of text) {
    if (character <= " " || character > "~" || character === "?" || character === "#" || character === "%") return null;
  }
  const segments = text.slice(LINK_PREFIX.length).split("/");
  if (segments.length === 1) {
    const code = normalizeInviteCode(segments[0]);
    return code === null ? null : { kind: "invite", inviteCode: code, canonical: LINK_PREFIX + code };
  }
  if (segments.length === 2 && segments[0] === "server" && isHex(segments[1], 32)) {
    return { kind: "server", serverId: segments[1], canonical: `${LINK_PREFIX}server/${segments[1]}` };
  }
  return null;
}
