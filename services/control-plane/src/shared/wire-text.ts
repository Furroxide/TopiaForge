/**
 * The text encodings the control plane shares with the game and the launcher (docs/internal/MultiplayerLiveSessions.md,
 * section 2): lowercase hexadecimal, unpadded base64url with zero unused bits, and display text measured in UTF-16
 * code units. Parsing is strict, so every value has exactly one accepted spelling.
 */

export type Bytes = Uint8Array<ArrayBuffer>;

const HEX_DIGITS = "0123456789abcdef";
const BASE64URL_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
const encoder = new TextEncoder();
const strictDecoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });

export function utf8(text: string): Bytes {
  return encoder.encode(text) as Bytes;
}

/** Decodes UTF-8, refusing malformed sequences and keeping a byte order mark so a canonical check sees it. */
export function decodeUtf8Strict(bytes: Uint8Array): string | null {
  try {
    return strictDecoder.decode(bytes);
  } catch {
    return null;
  }
}

export function toHex(bytes: Uint8Array): string {
  let text = "";
  for (const value of bytes) {
    text += HEX_DIGITS[value >> 4]! + HEX_DIGITS[value & 0xf]!;
  }
  return text;
}

/** Parses exactly `byteCount` bytes of lowercase hexadecimal. */
export function parseHex(text: unknown, byteCount: number): Bytes | null {
  if (typeof text !== "string" || text.length !== byteCount * 2) return null;
  const bytes = new Uint8Array(byteCount);
  for (let index = 0; index < byteCount; index++) {
    const high = HEX_DIGITS.indexOf(text[index * 2]!);
    const low = HEX_DIGITS.indexOf(text[index * 2 + 1]!);
    if (high < 0 || low < 0) return null;
    bytes[index] = (high << 4) | low;
  }
  return bytes;
}

export function isHex(text: unknown, byteCount: number): text is string {
  return parseHex(text, byteCount) !== null;
}

export function toBase64Url(bytes: Uint8Array): string {
  let text = "";
  let buffer = 0;
  let bits = 0;
  for (const value of bytes) {
    buffer = ((buffer << 8) | value) & 0xffff;
    bits += 8;
    while (bits >= 6) {
      bits -= 6;
      text += BASE64URL_ALPHABET[(buffer >> bits) & 0x3f]!;
    }
  }
  if (bits > 0) text += BASE64URL_ALPHABET[(buffer << (6 - bits)) & 0x3f]!;
  return text;
}

/**
 * Parses unpadded base64url that encodes exactly `byteCount` bytes. The unused low bits of the last character must be
 * zero, so no two spellings decode to the same bytes.
 */
export function parseBase64Url(text: unknown, byteCount: number): Bytes | null {
  if (typeof text !== "string" || text.length !== Math.floor((byteCount * 8 + 5) / 6)) return null;
  const bytes = new Uint8Array(byteCount);
  let written = 0;
  let buffer = 0;
  let bits = 0;
  for (const character of text) {
    const value = BASE64URL_ALPHABET.indexOf(character);
    if (value < 0) return null;
    buffer = ((buffer << 6) | value) & 0xffff;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      if (written === byteCount) return null;
      bytes[written++] = (buffer >> bits) & 0xff;
    }
  }
  if (written !== byteCount || (buffer & ((1 << bits) - 1)) !== 0) return null;
  return bytes;
}

/** The characters .NET's `char.IsWhiteSpace` accepts, so display text means the same in every runtime. */
function isWhiteSpace(code: number): boolean {
  return (
    (code >= 0x09 && code <= 0x0d) ||
    code === 0x20 ||
    code === 0x85 ||
    code === 0xa0 ||
    code === 0x1680 ||
    (code >= 0x2000 && code <= 0x200a) ||
    code === 0x2028 ||
    code === 0x2029 ||
    code === 0x202f ||
    code === 0x205f ||
    code === 0x3000
  );
}

/** Unicode category Cc, as .NET's `char.IsControl` reports it. */
function isControl(code: number): boolean {
  return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
}

/**
 * Whether `text` is display text: 1 to `maximum` UTF-16 code units, no surrounding whitespace, no control characters
 * and no unpaired surrogates.
 */
export function isDisplayText(text: unknown, maximum: number): text is string {
  if (typeof text !== "string" || text.length === 0 || text.length > maximum) return false;
  if (isWhiteSpace(text.charCodeAt(0)) || isWhiteSpace(text.charCodeAt(text.length - 1))) return false;
  for (let index = 0; index < text.length; index++) {
    const code = text.charCodeAt(index);
    if (isControl(code)) return false;
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = text.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      index++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}

export function concatBytes(...parts: readonly Uint8Array[]): Bytes {
  let length = 0;
  for (const part of parts) length += part.length;
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.length;
  }
  return result;
}

/** Compares two byte arrays in time that depends only on their lengths. */
export function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) difference |= left[index]! ^ right[index]!;
  return difference === 0;
}

export async function sha256(data: Uint8Array): Promise<Bytes> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", data as Bytes));
}

export async function sha256Hex(data: Uint8Array): Promise<string> {
  return toHex(await sha256(data));
}

/** Writes `value` as an unsigned big-endian integer of `length` bytes. */
export function uintBytes(value: number, length: number): Bytes {
  const bytes = new Uint8Array(length);
  let remaining = value;
  for (let index = length - 1; index >= 0; index--) {
    bytes[index] = remaining % 256;
    remaining = Math.floor(remaining / 256);
  }
  return bytes;
}

/** Reads an unsigned big-endian integer from `bytes`. */
export function readUint(bytes: Uint8Array, offset: number, length: number): number {
  let value = 0;
  for (let index = 0; index < length; index++) value = value * 256 + bytes[offset + index]!;
  return value;
}
