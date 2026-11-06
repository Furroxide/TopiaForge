import { describe, expect, it } from "vitest";
import { formatIp, inRanges, isLocalOnly, normalizeIp, ownScope, parseIp, parseIpRanges, prefixBucket } from "../src/shared/ip";
import { serviceKeys } from "../src/shared/keys";
import {
  CHALLENGE_TTL_SECONDS,
  bearer,
  hostTokenHash,
  issueChallenge,
  inviteHash,
  issueJoinTicket,
  newHostToken,
  readChallenge,
  readJoinTicket,
} from "../src/shared/tokens";
import { isDisplayText, isHex, parseBase64Url, toBase64Url, toHex, utf8 } from "../src/shared/wire-text";

const SERVICE_KEY = "dGVzdC1vbmx5IHNlcnZpY2Uga2V5LCBub3QgcmVhbCE";

describe("wire text", () => {
  it("round-trips base64url and refuses every other spelling", () => {
    const bytes = Uint8Array.from({ length: 32 }, (_, index) => index * 7);
    const text = toBase64Url(bytes);
    expect(text).toHaveLength(43);
    expect(parseBase64Url(text, 32)).toEqual(bytes);
    expect(parseBase64Url(text + "A", 32)).toBeNull();
    expect(parseBase64Url(text.slice(0, -1), 32)).toBeNull();
    expect(parseBase64Url(text.replace(/.$/, "B"), 32)).toBeNull();
    expect(parseBase64Url(text.slice(0, -1) + "=", 32)).toBeNull();
    expect(parseBase64Url("+".repeat(43), 32)).toBeNull();
  });

  it("reads only lowercase hexadecimal of the exact length", () => {
    expect(isHex("00ff", 2)).toBe(true);
    expect(isHex("00FF", 2)).toBe(false);
    expect(isHex("00f", 2)).toBe(false);
    expect(toHex(Uint8Array.of(0, 15, 255))).toBe("000fff");
  });

  it("measures display text in UTF-16 code units and refuses padding, controls and lone surrogates", () => {
    expect(isDisplayText("x".repeat(48), 48)).toBe(true);
    expect(isDisplayText("x".repeat(49), 48)).toBe(false);
    expect(isDisplayText("😀".repeat(24), 48)).toBe(true);
    expect(isDisplayText("😀".repeat(25), 48)).toBe(false);
    for (const text of ["", " x", "x ", "x ", "　x", "a\u0007b", "a\u0085b", "a\u009fb", "\ud800x", "x\udc00"]) {
      expect(isDisplayText(text, 48), JSON.stringify(text)).toBe(false);
    }
    expect(isDisplayText("﻿x", 48)).toBe(true);
    expect(isDisplayText("a\u007f", 48)).toBe(false);
  });
});

describe("addresses", () => {
  it("format IPv6 as RFC 5952 and an IPv4-mapped address as IPv4", () => {
    for (const [input, expected] of [
      ["2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1"],
      ["2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1"],
      ["2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1"],
      ["2001:0:0:1:0:0:0:1", "2001:0:0:1::1"],
      ["0:0:0:0:0:0:0:0", "::"],
      ["0:0:0:0:0:0:0:1", "::1"],
      ["1:0:0:0:0:0:0:0", "1::"],
      ["FE80::ABCD", "fe80::abcd"],
      ["::ffff:192.0.2.1", "192.0.2.1"],
      ["192.0.2.1", "192.0.2.1"],
    ] as const) {
      expect(normalizeIp(input)).toBe(expected);
    }
    for (const input of ["fe80::1%eth0", "[::1]", "1:2:3:4:5:6:7:8:9", "::g", "gggg::1", "1::2::3", "192.168.001.1", "256.1.1.1", "1.2.3", ""]) {
      expect(parseIp(input), input).toBeNull();
    }
  });

  it("bucket IPv4 by /24 and IPv6 by /56, treating mapped addresses as IPv4", () => {
    expect(prefixBucket(parseIp("198.51.100.7")!)).toBe("v4:198.51.100");
    expect(prefixBucket(parseIp("198.51.100.250")!)).toBe(prefixBucket(parseIp("198.51.100.7")!));
    expect(prefixBucket(parseIp("::ffff:198.51.100.9")!)).toBe("v4:198.51.100");
    expect(prefixBucket(parseIp("2001:db8:aa:bb00::1")!)).toBe("v6:20010db800aabb");
    expect(prefixBucket(parseIp("2001:db8:aa:bbff:ffff::1")!)).toBe(prefixBucket(parseIp("2001:db8:aa:bb00::1")!));
    expect(prefixBucket(parseIp("2001:db8:aa:bc00::1")!)).not.toBe(prefixBucket(parseIp("2001:db8:aa:bb00::1")!));
  });

  it("recognize addresses that can only be local to the sender", () => {
    for (const address of ["0.1.2.3", "10.1.2.3", "127.0.0.1", "100.64.0.1", "100.127.255.255", "169.254.1.1", "172.16.0.1", "172.31.255.255", "192.168.1.1", "224.0.0.1", "255.255.255.255", "::", "::1", "fc00::1", "fd12::1", "fe80::1", "ff02::1", "::ffff:10.0.0.1"]) {
      expect(isLocalOnly(parseIp(address)!), address).toBe(true);
    }
    for (const address of ["100.128.0.1", "172.32.0.1", "198.51.100.1", "203.0.113.9", "2001:db8::1", "2606:4700::1"]) {
      expect(isLocalOnly(parseIp(address)!), address).toBe(false);
    }
    expect(formatIp(parseIp("::ffff:10.0.0.1")!)).toBe("10.0.0.1");
  });

  it("scope an own address as the whole IPv4 address or the IPv6 /64", () => {
    expect(ownScope(parseIp("198.51.100.7")!)).toBe("v4:198.51.100.7");
    expect(ownScope(parseIp("::ffff:198.51.100.7")!)).toBe("v4:198.51.100.7");
    expect(ownScope(parseIp("198.51.100.8")!)).not.toBe(ownScope(parseIp("198.51.100.7")!));
    expect(ownScope(parseIp("2001:db8:1:2::10")!)).toBe("v6/64:20010db800010002");
    expect(ownScope(parseIp("2001:db8:1:2:ffff:ffff:ffff:abcd")!)).toBe(ownScope(parseIp("2001:db8:1:2::10")!));
    expect(ownScope(parseIp("2001:db8:1:3::10")!)).not.toBe(ownScope(parseIp("2001:db8:1:2::10")!));
  });
});

describe("address ranges", () => {
  it("read space-separated CIDR blocks and match addresses of either family against them", () => {
    const ranges = parseIpRanges("198.51.100.0/24 104.16.0.0/13 2001:db8::/32 2a06:98c0::/29")!;
    expect(ranges).toHaveLength(4);
    for (const inside of ["198.51.100.0", "198.51.100.255", "104.23.255.255", "::ffff:104.16.0.1", "2001:db8:ffff::1", "2a06:98c7:ffff::1"]) {
      expect(inRanges(parseIp(inside)!, ranges), inside).toBe(true);
    }
    for (const outside of ["198.51.101.0", "104.24.0.0", "104.15.255.255", "2001:db9::1", "2a06:98c8::1", "2a06:98bf::1"]) {
      expect(inRanges(parseIp(outside)!, ranges), outside).toBe(false);
    }
    expect(inRanges(parseIp("198.51.100.1")!, [])).toBe(false);
  });

  it("refuse any list that is not exactly right, so a typo can only allow less", () => {
    for (const text of [
      "",
      " 198.51.100.0/24",
      "198.51.100.0/24 ",
      "198.51.100.0/24  2001:db8::/32",
      "198.51.100.0/24,2001:db8::/32",
      "198.51.100.1/24",
      "198.51.100.0/33",
      "198.51.100.0/7",
      "0.0.0.0/8",
      "10.0.0.0/8",
      "198.51.100.0/024",
      "198.51.100.0",
      "2001:DB8::/32",
      "2001:db8:0::/32",
      "2001:db8::/15",
      "2001:db8::/129",
      "fc00::/7",
      "::ffff:198.51.100.0/120",
      "2001:db8::/32/1",
      Array.from({ length: 65 }, (_, index) => `198.51.${index}.0/24`).join(" "),
      undefined,
      42,
    ]) {
      expect(parseIpRanges(text), String(text)).toBeNull();
    }
  });
});

describe("credentials", () => {
  it("issue challenges bound to a signing key, valid once checked against it and only until expiry", async () => {
    const keys = (await serviceKeys(SERVICE_KEY))!;
    const signingKey = new Uint8Array(32).fill(1);
    const other = new Uint8Array(32).fill(2);
    const issued = await issueChallenge(keys.challenge, signingKey, new Uint8Array(16).fill(9), 1000);
    expect((await readChallenge(keys.challenge, issued, signingKey, 1000))?.expiresAt).toBe(1000 + CHALLENGE_TTL_SECONDS);
    expect(await readChallenge(keys.challenge, issued, other, 1000)).toBeNull();
    expect(await readChallenge(keys.challenge, issued, signingKey, 1000 + CHALLENGE_TTL_SECONDS)).toBeNull();
    expect(await readChallenge(keys.ticket, issued, signingKey, 1000)).toBeNull();
    const tampered = toBase64Url(parseBase64Url(issued, 41)!.map((value, index) => (index === 20 ? value ^ 1 : value)));
    expect(await readChallenge(keys.challenge, tampered, signingKey, 1000)).toBeNull();
  });

  it("issue join tickets that carry their session, invite and facts under a MAC", async () => {
    const keys = (await serviceKeys(SERVICE_KEY))!;
    const invite = await inviteHash(keys.invite, "RJ9JMV1V09");
    expect(invite).toMatch(/^[0-9a-f]{32}$/);
    expect(await inviteHash(keys.invite, "RJ9JMV1V08")).not.toBe(invite);
    const ticket = { serverId: "a".repeat(64), epoch: "c".repeat(32), ticketId: "b".repeat(32), invite, expiresAt: 5000, relayOnly: true };
    const text = await issueJoinTicket(keys.ticket, ticket);
    expect(text).toMatch(/^tfj1_[A-Za-z0-9_-]{142}$/);
    expect(await readJoinTicket(keys.ticket, text, 4999)).toEqual(ticket);
    expect(await readJoinTicket(keys.ticket, text, 5000)).toBeNull();
    expect(await readJoinTicket(keys.challenge, text, 1)).toBeNull();
    const withoutInvite = { ...ticket, invite: null, relayOnly: false };
    expect(await readJoinTicket(keys.ticket, await issueJoinTicket(keys.ticket, withoutInvite), 1)).toEqual(withoutInvite);
    // Every byte is covered by the MAC: the epoch, the invite hash and the flags among them.
    for (const offset of [40, 70, 89]) {
      const body = parseBase64Url(text.slice(5), 106)!;
      body[offset] = body[offset]! ^ 1;
      expect(await readJoinTicket(keys.ticket, `tfj1_${toBase64Url(body)}`, 1), String(offset)).toBeNull();
    }
  });

  it("hash only well-formed host tokens and read only bearer credentials", async () => {
    const token = newHostToken(new Uint8Array(32).fill(7));
    expect(token.startsWith("tfh1_")).toBe(true);
    expect(await hostTokenHash(token)).toMatch(/^[0-9a-f]{64}$/);
    expect(await hostTokenHash(token + "A")).toBeNull();
    expect(await hostTokenHash(token.replace("tfh1_", "tfj1_"))).toBeNull();
    expect(bearer(`Bearer ${token}`)).toBe(token);
    expect(bearer(`bearer ${token}`)).toBeNull();
    expect(bearer("Bearer a b")).toBeNull();
    expect(bearer(null)).toBeNull();
  });

  it("derive no keys from a malformed service key", () => {
    expect(serviceKeys(undefined)).toBeNull();
    expect(serviceKeys("short")).toBeNull();
    expect(serviceKeys(SERVICE_KEY.slice(0, -1) + "F")).toBeNull();
    expect(utf8("x")).toHaveLength(1);
  });
});
