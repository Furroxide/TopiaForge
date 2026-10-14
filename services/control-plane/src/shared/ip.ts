/**
 * IP addresses, handled only in memory. The service never stores, logs or returns a raw address: rate limits key on a
 * keyed hash of the address's prefix (see keys.ts), and signaling compares candidate addresses by keyed hash.
 */

export interface IpAddress {
  readonly family: 4 | 6;
  readonly bytes: Uint8Array;
}

/** Parses an IPv4 address in dotted decimal or an IPv6 address in text form, without zone or brackets. */
export function parseIp(text: unknown): IpAddress | null {
  if (typeof text !== "string" || text.length === 0 || text.length > 45) return null;
  if (text.includes(":")) return parseIPv6(text);
  const v4 = parseIPv4(text);
  return v4 === null ? null : { family: 4, bytes: v4 };
}

/**
 * The canonical text of an address: dotted decimal for IPv4, RFC 5952 for IPv6, and an IPv4-mapped IPv6 address as
 * the IPv4 address it maps.
 */
export function formatIp(address: IpAddress): string {
  const mapped = unmapIPv4(address);
  if (mapped.family === 4) return Array.from(mapped.bytes).join(".");
  const groups: number[] = [];
  for (let index = 0; index < 8; index++) groups.push((mapped.bytes[index * 2]! << 8) | mapped.bytes[index * 2 + 1]!);
  // The longest run of two or more zero groups is compressed; the first wins a tie.
  let bestStart = -1;
  let bestLength = 1;
  for (let index = 0; index < 8; ) {
    if (groups[index] !== 0) {
      index++;
      continue;
    }
    const start = index;
    while (index < 8 && groups[index] === 0) index++;
    if (index - start > bestLength) {
      bestStart = start;
      bestLength = index - start;
    }
  }
  let text = "";
  for (let index = 0; index < 8; index++) {
    if (index === bestStart) {
      text += "::";
      index += bestLength - 1;
      continue;
    }
    if (text.length > 0 && !text.endsWith(":")) text += ":";
    text += groups[index]!.toString(16);
  }
  return text;
}

/** Parses and re-formats, or null. */
export function normalizeIp(text: unknown): string | null {
  const address = parseIp(text);
  return address === null ? null : formatIp(address);
}

/** An IPv4-mapped IPv6 address becomes the IPv4 address it maps; anything else is unchanged. */
export function unmapIPv4(address: IpAddress): IpAddress {
  if (address.family === 6 && isMappedIPv4(address.bytes)) return { family: 4, bytes: address.bytes.slice(12) };
  return address;
}

/**
 * The rate-limit bucket an address falls in: its /24 for IPv4 and its /56 for IPv6, as text that names the family.
 * The text only ever feeds a keyed hash; it is never stored.
 */
export function prefixBucket(address: IpAddress): string {
  const plain = unmapIPv4(address);
  if (plain.family === 4) return `v4:${Array.from(plain.bytes.slice(0, 3)).join(".")}`;
  return `v6:${Array.from(plain.bytes.slice(0, 7), (value) => value.toString(16).padStart(2, "0")).join("")}`;
}

/**
 * Whether an address can only be local to the sender: unspecified, loopback, private, shared (CGNAT), link-local,
 * unique-local, multicast or broadcast. A relay's address is public, so a relay candidate never has one of these.
 */
export function isLocalOnly(address: IpAddress): boolean {
  const plain = unmapIPv4(address);
  const b = plain.bytes;
  if (plain.family === 4) {
    return (
      b[0] === 0 ||
      b[0] === 10 ||
      b[0] === 127 ||
      (b[0] === 100 && (b[1]! & 0xc0) === 64) ||
      (b[0] === 169 && b[1] === 254) ||
      (b[0] === 172 && (b[1]! & 0xf0) === 16) ||
      (b[0] === 192 && b[1] === 168) ||
      b[0]! >= 224
    );
  }
  const allZeroUpTo15 = b.slice(0, 15).every((value) => value === 0);
  return (
    (allZeroUpTo15 && (b[15] === 0 || b[15] === 1)) ||
    (b[0]! & 0xfe) === 0xfc ||
    (b[0] === 0xfe && (b[1]! & 0xc0) === 0x80) ||
    b[0] === 0xff
  );
}

function isMappedIPv4(bytes: Uint8Array): boolean {
  for (let index = 0; index < 10; index++) if (bytes[index] !== 0) return false;
  return bytes[10] === 0xff && bytes[11] === 0xff;
}

function parseIPv4(text: string): Uint8Array | null {
  const parts = text.split(".");
  if (parts.length !== 4) return null;
  const bytes = new Uint8Array(4);
  for (let index = 0; index < 4; index++) {
    const part = parts[index]!;
    if (!/^(0|[1-9][0-9]{0,2})$/.test(part)) return null;
    const value = Number(part);
    if (value > 255) return null;
    bytes[index] = value;
  }
  return bytes;
}

function parseIPv6(text: string): IpAddress | null {
  if (!/^[0-9A-Fa-f:.]+$/.test(text)) return null;
  const doubleColon = text.indexOf("::");
  if (doubleColon !== text.lastIndexOf("::") && doubleColon >= 0) return null;
  let tail: number[] = [];
  const parseGroups = (section: string, allowIPv4: boolean): number[] | null => {
    if (section === "") return [];
    const pieces = section.split(":");
    const groups: number[] = [];
    for (let index = 0; index < pieces.length; index++) {
      const piece = pieces[index]!;
      if (allowIPv4 && index === pieces.length - 1 && piece.includes(".")) {
        const v4 = parseIPv4(piece);
        if (v4 === null) return null;
        groups.push((v4[0]! << 8) | v4[1]!, (v4[2]! << 8) | v4[3]!);
        continue;
      }
      if (!/^[0-9A-Fa-f]{1,4}$/.test(piece)) return null;
      groups.push(parseInt(piece, 16));
    }
    return groups;
  };
  let head: number[] | null;
  if (doubleColon >= 0) {
    head = parseGroups(text.slice(0, doubleColon), false);
    const parsedTail = parseGroups(text.slice(doubleColon + 2), true);
    if (head === null || parsedTail === null || head.length + parsedTail.length > 7) return null;
    tail = parsedTail;
  } else {
    head = parseGroups(text, true);
    if (head === null || head.length !== 8) return null;
  }
  const groups = [...head, ...new Array<number>(8 - head.length - tail.length).fill(0), ...tail];
  const bytes = new Uint8Array(16);
  groups.forEach((group, index) => {
    bytes[index * 2] = group >> 8;
    bytes[index * 2 + 1] = group & 0xff;
  });
  return { family: 6, bytes };
}
