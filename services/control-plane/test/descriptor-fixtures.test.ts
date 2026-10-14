/**
 * Replays tests/fixtures/multiplayer/descriptor, the vectors the game and the launcher replay too: every valid
 * descriptor verifies and signs back to the served bytes, and every tampered one is refused.
 */
import { describe, expect, it } from "vitest";
import fixture from "../../../tests/fixtures/multiplayer/descriptor/cases.json";
import { stringify, type JsonObject } from "../src/shared/canonical-json";
import { MAXIMUM_DESCRIPTOR_BYTES, verifySignedDescriptor } from "../src/shared/descriptor";
import { concatBytes, parseHex, sha256Hex, utf8 } from "../src/shared/wire-text";
import { identityFromSeed, sign, signedDescriptor } from "./support/api";

const keys = new Map(fixture.keys.map((key) => [key.id, key]));

describe("the shared descriptor fixtures", () => {
  it("sign under the same context label", () => {
    expect(fixture.context).toBe("TopiaForge server descriptor v1\n");
    expect(fixture.valid.length).toBeGreaterThanOrEqual(3);
    expect(fixture.invalid.length).toBeGreaterThanOrEqual(30);
  });

  for (const testCase of fixture.valid) {
    it(`verify the valid case ${testCase.id}`, async () => {
      const key = keys.get(testCase.key)!;
      const bytes = utf8(testCase.signedBytes);
      const verified = await verifySignedDescriptor(bytes, testCase.serverId);
      expect(verified.ok, verified.ok ? "" : verified.error).toBe(true);
      if (!verified.ok) return;
      expect(verified.signed.descriptor.serverId).toBe(key.serverId);
      expect(verified.signed.descriptor.packageDigest).toBe(testCase.packageDigest);
      expect(verified.signed.sha256).toBe(testCase.descriptorSha256);
      expect(verified.signed.descriptor.name).toBe(testCase.descriptor.name);
      expect((await verifySignedDescriptor(bytes, null)).ok).toBe(true);
    });

    it(`write and sign the valid case ${testCase.id} byte for byte`, async () => {
      const key = keys.get(testCase.key)!;
      const descriptor = testCase.descriptor as unknown as JsonObject;
      expect(stringify(descriptor)).toBe(testCase.canonicalDescriptor);
      const host = await identityFromSeed(key.id, parseHex(key.signingSeed, 32)!);
      expect(host.signingKey).toBe(key.signingKey);
      const signature = await sign(host.privateKey, concatBytes(utf8(fixture.context), utf8(testCase.canonicalDescriptor)));
      expect(signature).toBe(testCase.signature);
      expect(stringify({ descriptor, signature })).toBe(testCase.signedBytes);
    });
  }

  for (const testCase of fixture.invalid) {
    it(`refuse the invalid case ${testCase.id}`, async () => {
      const bytes = "bytesHex" in testCase ? parseHex(testCase.bytesHex, testCase.bytesHex.length / 2)! : utf8(testCase.bytes);
      const verified = await verifySignedDescriptor(bytes, testCase.expectedServerId);
      expect(verified.ok, testCase.about).toBe(false);
    });
  }
});

describe("descriptor bounds and strictness", () => {
  it("fit 512 packages within 256 KiB and refuse 513", async () => {
    const host = await identityFromSeed("bounds", parseHex(keys.get("host-a")!.signingSeed, 32)!);
    const sha256 = await sha256Hex(utf8("package"));
    const packages = (count: number) =>
      Array.from({ length: count }, (_, index) => ({ id: `mod.${String(index).padStart(4, "0")}`, version: "1.0.0", sha256, role: "implicit" }));
    const most = await signedDescriptor(host, { packages: packages(512) });
    expect(utf8(most).length).toBeLessThanOrEqual(MAXIMUM_DESCRIPTOR_BYTES);
    expect((await verifySignedDescriptor(utf8(most), host.serverId)).ok).toBe(true);
    const tooMany = await signedDescriptor(host, { packages: packages(513) });
    expect((await verifySignedDescriptor(utf8(tooMany), host.serverId)).ok).toBe(false);
  });

  it("refuse a trailing newline in a game build, package id or version", async () => {
    const host = await identityFromSeed("newline", parseHex(keys.get("host-b")!.signingSeed, 32)!);
    const sha256 = await sha256Hex(utf8("package"));
    for (const document of [
      await signedDescriptor(host, { gameBuild: "2545\n" }),
      await signedDescriptor(host, { protocolVersion: "1.0.0\n" }),
      await signedDescriptor(host, { packages: [{ id: "a.mod\n", version: "1.0.0", sha256, role: "implicit" }] }),
      await signedDescriptor(host, { packages: [{ id: "a.mod", version: "1.0.0\n", sha256, role: "implicit" }] }),
    ]) {
      expect((await verifySignedDescriptor(utf8(document), host.serverId)).ok).toBe(false);
    }
  });

  it("refuse documents larger than the bound before parsing them", async () => {
    const oversized = new Uint8Array(MAXIMUM_DESCRIPTOR_BYTES + 1).fill(0x20);
    expect((await verifySignedDescriptor(oversized, null)).ok).toBe(false);
  });
});
