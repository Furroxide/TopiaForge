import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { verifySignedDescriptor } from "../src/shared/descriptor";
import { utf8 } from "../src/shared/wire-text";
import { call, identity, register, type HostIdentity } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("directory");
});
afterEach(() => endScenario());

async function hosts(count: number, label: string, options: Parameters<typeof register>[2] = {}): Promise<HostIdentity[]> {
  const made: HostIdentity[] = [];
  for (let index = 0; index < count; index++) {
    const host = await identity(`${label}-${scenario.day}-${index}`);
    // Each host registers from its own /24, so the registration limit never interferes.
    await register(host, `198.18.${index}.${(scenario.day % 250) + 1}`, options);
    made.push(host);
  }
  return made;
}

describe("the server browser", () => {
  it("pages through every public server in id order across the shards", async () => {
    const registered = await hosts(7, "paging");
    const expected = registered.map((host) => host.serverId).sort();
    const seen: string[] = [];
    let after: string | null = null;
    for (let page = 0; page < 10; page++) {
      const response = await call("GET", `/v1/servers?build=2545&protocol=1&limit=3${after === null ? "" : `&after=${after}`}`, { ip: scenario.otherIp });
      expect(response.status).toBe(200);
      const servers = response.json["servers"] as Record<string, string>[];
      seen.push(...servers.map((server) => server["serverId"]!));
      after = response.json["next"] as string | null;
      if (after === null) break;
      expect(after).toBe(servers[servers.length - 1]!["serverId"]);
    }
    expect(seen).toEqual(expected);
  });

  it("keeps builds and protocol majors apart and leaves invite-only servers out", async () => {
    const [current] = await hosts(1, "current");
    const [nextBuild] = await hosts(1, "next-build", { gameBuild: "2546" });
    const [nextProtocol] = await hosts(1, "next-protocol", { protocolVersion: "2.1.0" });
    const [hidden] = await hosts(1, "hidden", { visibility: "invite" });
    const ids = async (query: string) =>
      ((await call("GET", `/v1/servers?${query}`, { ip: scenario.otherIp })).json["servers"] as Record<string, string>[]).map((server) => server["serverId"]);
    expect(await ids("build=2545&protocol=1")).toEqual([current!.serverId]);
    expect(await ids("build=2546&protocol=1")).toEqual([nextBuild!.serverId]);
    expect(await ids("build=2545&protocol=2")).toEqual([nextProtocol!.serverId]);
    expect(await ids("build=2545&protocol=1")).not.toContain(hidden!.serverId);
  });

  it("returns listings that hold nothing but the descriptor's summary and the heartbeat's player count", async () => {
    const [host] = await hosts(1, "shape");
    const response = await call("GET", "/v1/servers?build=2545&protocol=1", { ip: scenario.otherIp });
    const listing = (response.json["servers"] as Record<string, unknown>[])[0]!;
    expect(Object.keys(listing).sort()).toEqual(
      [
        "descriptorSha256",
        "gameBuild",
        "gamemode",
        "hostServesPackages",
        "maxPlayers",
        "name",
        "packageCount",
        "packageDigest",
        "passwordRequired",
        "players",
        "protocolVersion",
        "relayOnly",
        "runtimeVersion",
        "schema",
        "serverId",
        "updatedAt",
        "visibility",
        "voice",
        "world",
      ].sort(),
    );
    expect(listing).toMatchObject({ schema: "topiaforge.server-listing/1", serverId: host!.serverId, players: 0, packageCount: 1 });
  });

  it("refuses malformed queries", async () => {
    for (const query of [
      "",
      "build=2545",
      "protocol=1",
      "build=2545&protocol=01",
      "build=25 45&protocol=1",
      "build=2545&protocol=1&limit=0",
      "build=2545&protocol=1&limit=101",
      "build=2545&protocol=1&after=ABC",
      "build=2545&protocol=1&build=2546",
      "build=2545&protocol=1&sort=name",
    ]) {
      const response = await call("GET", `/v1/servers${query === "" ? "" : `?${query}`}`, { ip: scenario.otherIp });
      expect(response.status, query).toBe(400);
    }
  });

  it("serves a public server's signed descriptor, verifiable against its id", async () => {
    const [host] = await hosts(1, "detail");
    const response = await call("GET", `/v1/servers/${host!.serverId}`, { ip: scenario.otherIp });
    expect(response.status).toBe(200);
    const verified = await verifySignedDescriptor(utf8(response.json["descriptor"] as string), host!.serverId);
    expect(verified.ok).toBe(true);
    if (verified.ok) expect(response.json["descriptorSha256"]).toBe(verified.signed.sha256);
    expect((response.json["listing"] as Record<string, string>)["serverId"]).toBe(host!.serverId);
    expect((await call("GET", `/v1/servers/${"0".repeat(64)}`, { ip: scenario.otherIp })).status).toBe(404);
    expect((await call("GET", `/v1/servers/${host!.serverId.toUpperCase()}`, { ip: scenario.otherIp })).status).toBe(404);
  });
});
