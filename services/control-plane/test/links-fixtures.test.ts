/**
 * Replays tests/fixtures/multiplayer/links. The service reads invite and server links and formats invite links; a
 * direct link carries a host address, so the service refuses to read one at all.
 */
import { describe, expect, it } from "vitest";
import fixture from "../../../tests/fixtures/multiplayer/links/cases.json";
import {
  INVITE_ALPHABET,
  displayInviteCode,
  inviteCodeFrom,
  inviteLink,
  isCanonicalInviteCode,
  normalizeInviteCode,
  parseJoinLink,
} from "../src/shared/invite-code";

describe("the shared link fixtures", () => {
  it("use the same Crockford alphabet", () => {
    expect(fixture.alphabet).toBe(INVITE_ALPHABET);
  });

  for (const testCase of fixture.inviteCodes) {
    it(`normalize the invite code ${JSON.stringify(testCase.input)}`, () => {
      const code = normalizeInviteCode(testCase.input);
      expect(code).toBe(testCase.canonical);
      if (code !== null && "display" in testCase) expect(displayInviteCode(code)).toBe(testCase.display);
    });
  }

  for (const testCase of fixture.valid) {
    it(`read the valid link ${testCase.input}`, () => {
      const link = parseJoinLink(testCase.input);
      if (testCase.kind === "direct") {
        expect(link).toBeNull();
        return;
      }
      expect(link).not.toBeNull();
      expect(link!.kind).toBe(testCase.kind);
      expect(link!.canonical).toBe(testCase.canonical);
      if (link!.kind === "invite") {
        expect(link!.inviteCode).toBe(testCase.inviteCode);
        expect(inviteLink(link!.inviteCode)).toBe(testCase.canonical);
      } else {
        expect(link!.serverId).toBe((testCase as { serverId: string }).serverId);
      }
      expect(parseJoinLink(link!.canonical)?.canonical).toBe(link!.canonical);
    });
  }

  for (const testCase of fixture.invalid) {
    it(`refuse the invalid link: ${testCase.about}`, () => {
      expect(parseJoinLink(testCase.input)).toBeNull();
    });
  }

  it("cover every link kind and at least the documented invalid forms", () => {
    expect(new Set(fixture.valid.map((testCase) => testCase.kind))).toEqual(new Set(["invite", "server", "direct"]));
    expect(fixture.invalid.length).toBeGreaterThanOrEqual(24);
  });
});

describe("invite codes", () => {
  it("fold ASCII case only, so no other script's letters stand in for a code character", () => {
    expect(normalizeInviteCode("abcde12345")).toBe("ABCDE12345");
    for (const input of ["ABCDEſ2345", "ABCDEı2345", "ABCDEß2345", "ＡBCDE12345", "ABCDE1234😀"]) expect(normalizeInviteCode(input)).toBeNull();
  });

  it("refuse inputs longer than 32 characters even when separators would shorten them", () => {
    expect(normalizeInviteCode(`ABCDE${"-".repeat(22)}12345`)).toBe("ABCDE12345");
    expect(normalizeInviteCode(`ABCDE${"-".repeat(23)}12345`)).toBeNull();
  });

  it("draw each character from five random bits", () => {
    expect(inviteCodeFrom(Uint8Array.from([0, 1, 31, 32, 63, 64, 255, 16, 17, 18]))).toBe("01Z0Z0ZGHJ");
    const code = inviteCodeFrom(new Uint8Array(10).fill(0xa5));
    expect(isCanonicalInviteCode(code)).toBe(true);
  });
});
