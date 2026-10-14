import { runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { call, identity, register, registrationBody, registrationProof, signedDescriptor, type HostIdentity } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("hosting");
});
afterEach(() => endScenario());

const seconds = () => Math.floor(scenario.clock.now() / 1000);
const browse = (query = "build=2545&protocol=1") => call("GET", `/v1/servers?${query}`, { ip: scenario.otherIp });
const listedIds = async (query?: string) => ((await browse(query)).json["servers"] as { serverId: string }[]).map((server) => server.serverId);
const hostSession = (host: HostIdentity) => env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));

async function challengeFor(host: HostIdentity): Promise<string> {
  const response = await call("POST", "/v1/hosts/challenge", { ip: scenario.ip, body: { signingKey: host.signingKey } });
  expect(response.status).toBe(200);
  return response.json["challenge"] as string;
}

describe("host challenges", () => {
  it("issue a single-use challenge that expires in two minutes", async () => {
    const host = await identity("challenge");
    const response = await call("POST", "/v1/hosts/challenge", { ip: scenario.ip, body: { signingKey: host.signingKey } });
    expect(response.status).toBe(200);
    expect(response.json).toEqual({ challenge: expect.stringMatching(/^[A-Za-z0-9_-]{55}$/), expiresAt: seconds() + 120 });
    expect(response.headers.get("cache-control")).toBe("no-store");
  });

  it("refuse malformed requests without saying why", async () => {
    const host = await identity("malformed");
    const refusals: [number, string, Parameters<typeof call>[2]][] = [
      [400, "invalid-request", { body: {} }],
      [400, "invalid-request", { body: { signingKey: "short" } }],
      [400, "invalid-request", { body: { signingKey: host.signingKey, extra: true } }],
      [400, "invalid-request", { body: `{"signingKey": "${host.signingKey}"}` }],
      [415, "unsupported-media-type", { body: { signingKey: host.signingKey }, headers: { "content-type": "text/plain" } }],
      [413, "payload-too-large", { body: `{"signingKey":"${"A".repeat(2000)}"}` }],
    ];
    for (const [status, code, options] of refusals) {
      const response = await call("POST", "/v1/hosts/challenge", { ip: scenario.ip, ...options });
      expect(response.status).toBe(status);
      expect(response.json).toEqual({ error: { code, message: expect.any(String) } });
      expect(response.text).not.toContain(host.signingKey.slice(0, 12));
    }
  });
});

describe("host registration", () => {
  it("registers a public host with a proof, returns a token and an invite, and lists it", async () => {
    const host = await identity("public");
    const { response, token } = await register(host, scenario.ip);
    expect(response.json).toMatchObject({ serverId: host.serverId, listed: true, heartbeatSeconds: 30, listingTtlSeconds: 90, expiresAt: seconds() + 90 });
    expect(token).toMatch(/^tfh1_[A-Za-z0-9_-]{43}$/);
    const invite = response.json["invite"] as Record<string, string>;
    expect(invite["code"]).toMatch(/^[0-9A-HJKMNP-TV-Z]{10}$/);
    expect(invite["display"]).toBe(`${invite["code"]!.slice(0, 5)}-${invite["code"]!.slice(5)}`);
    expect(invite["link"]).toBe(`topiaforge://join/${invite["code"]}`);
    expect(await listedIds()).toContain(host.serverId);
  });

  it("registers an invite-only host without listing it anywhere", async () => {
    const host = await identity("invite-only");
    const { response } = await register(host, scenario.ip, { visibility: "invite" });
    expect(response.json["listed"]).toBe(false);
    expect(await listedIds()).not.toContain(host.serverId);
    expect((await call("GET", `/v1/servers/${host.serverId}`, { ip: scenario.ip })).status).toBe(404);
  });

  it("refuses LAN-only sessions and invalid descriptors", async () => {
    const host = await identity("lan");
    const challenge = await challengeFor(host);
    const lan = registrationBody(challenge, await signedDescriptor(host, { visibility: "lan" }), false);
    const lanResponse = await call("POST", "/v1/hosts", { ip: scenario.ip, body: { ...lan, proof: await registrationProof(host, lan) } });
    expect(lanResponse.json["error"]).toMatchObject({ code: "unsupported-visibility" });
    const tampered = (await signedDescriptor(host)).replace("Server lan", "Server LAN");
    const bad = registrationBody(await challengeFor(host), tampered, false);
    const badResponse = await call("POST", "/v1/hosts", { ip: scenario.ip, body: { ...bad, proof: await registrationProof(host, bad) } });
    expect(badResponse.status).toBe(400);
    expect(badResponse.json["error"]).toMatchObject({ code: "invalid-descriptor" });
  });

  it("refuses a challenge for another key, an expired challenge and a used one", async () => {
    const host = await identity("challenged");
    const other = await identity("someone-else");
    const descriptor = await signedDescriptor(host);
    const attempt = async (challenge: string) => {
      const body = registrationBody(challenge, descriptor, false);
      return call("POST", "/v1/hosts", { ip: scenario.ip, body: { ...body, proof: await registrationProof(host, body) } });
    };
    expect((await attempt(await challengeFor(other))).json["error"]).toMatchObject({ code: "challenge-invalid" });
    const stale = await challengeFor(host);
    scenario.clock.advance(121);
    expect((await attempt(stale)).json["error"]).toMatchObject({ code: "challenge-invalid" });
    const fresh = await challengeFor(host);
    expect((await attempt(fresh)).status).toBe(201);
    const replay = await attempt(fresh);
    expect(replay.status).toBe(401);
    expect(replay.json["error"]).toMatchObject({ code: "challenge-invalid" });
  });

  it("refuses a proof by another key or over a different registration", async () => {
    const host = await identity("proven");
    const intruder = await identity("intruder");
    const descriptor = await signedDescriptor(host);
    const body = registrationBody(await challengeFor(host), descriptor, false);
    const forged = await call("POST", "/v1/hosts", { ip: scenario.ip, body: { ...body, proof: await registrationProof(intruder, body) } });
    expect(forged.json["error"]).toMatchObject({ code: "proof-invalid" });
    const flipped = registrationBody(body["challenge"] as string, descriptor, true);
    const swapped = await call("POST", "/v1/hosts", { ip: scenario.ip, body: { ...flipped, proof: await registrationProof(host, body) } });
    expect(swapped.json["error"]).toMatchObject({ code: "proof-invalid" });
  });
});

describe("heartbeats and the listing TTL", () => {
  it("keep a session alive, and a lapsed session needs a new registration", async () => {
    const host = await identity("heartbeat");
    const { token } = await register(host, scenario.ip);
    scenario.clock.advance(30);
    const beat = await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 3 } });
    expect(beat.json).toEqual({ expiresAt: seconds() + 90, listed: true, heartbeatSeconds: 30 });
    const listing = ((await browse()).json["servers"] as Record<string, unknown>[]).find((server) => server["serverId"] === host.serverId);
    expect(listing).toMatchObject({ players: 3, updatedAt: seconds() });
    scenario.clock.advance(89);
    expect(await listedIds()).toContain(host.serverId);
    scenario.clock.advance(1);
    expect(await listedIds()).not.toContain(host.serverId);
    const late = await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 3 } });
    expect(late.status).toBe(401);
  });

  it("refuse wrong tokens, heartbeats closer than five seconds and more players than the maximum", async () => {
    const host = await identity("strict-heartbeat");
    const { token } = await register(host, scenario.ip, { maxPlayers: 4 });
    const path = `/v1/hosts/${host.serverId}/heartbeat`;
    expect((await call("POST", path, { ip: scenario.ip, body: { players: 1 } })).status).toBe(401);
    expect((await call("POST", path, { ip: scenario.ip, token: `tfh1_${"A".repeat(43)}`, body: { players: 1 } })).status).toBe(401);
    const early = await call("POST", path, { ip: scenario.ip, token, body: { players: 1 } });
    expect(early.status).toBe(429);
    expect(early.headers.get("retry-after")).toBe("5");
    scenario.clock.advance(10);
    expect((await call("POST", path, { ip: scenario.ip, token, body: { players: 5 } })).status).toBe(400);
    expect((await call("POST", path, { ip: scenario.ip, token, body: { players: 4 } })).status).toBe(200);
  });
});

describe("the session alarm", () => {
  it("delists the host, revokes its invites and deletes every byte of its state", async () => {
    const host = await identity("alarm");
    const { response, token } = await register(host, scenario.ip);
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    scenario.clock.advance(121);
    expect(await runDurableObjectAlarm(hostSession(host))).toBe(true);
    const tables = await runInDurableObject(hostSession(host), (_, state) =>
      state.storage.sql.exec<{ name: string }>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE '\\_cf\\_%' ESCAPE '\\' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\'").toArray(),
    );
    expect(tables).toEqual([]);
    expect(await runDurableObjectAlarm(hostSession(host))).toBe(false);
    expect((await call("POST", "/v1/invites/resolve", { ip: scenario.otherIp, body: { code } })).status).toBe(404);
    expect((await call("GET", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token })).status).toBe(401);
    const shard = env.DIRECTORY_SHARDS.get(env.DIRECTORY_SHARDS.idFromName(`directory/2545/1/${parseInt(host.serverId[0]!, 16)}`));
    const rows = await runInDurableObject(shard, (_, state) => state.storage.sql.exec("SELECT server_id FROM listing WHERE server_id = ?", host.serverId).toArray());
    expect(rows).toEqual([]);
  });

  it("leaves a heartbeating session alone", async () => {
    const host = await identity("alarm-alive");
    const { token } = await register(host, scenario.ip);
    scenario.clock.advance(60);
    await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 1 } });
    scenario.clock.advance(61);
    await runDurableObjectAlarm(hostSession(host));
    expect((await call("GET", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token })).status).toBe(200);
  });
});

describe("descriptor updates, ending and re-registration", () => {
  it("move the listing with the visibility and refuse an older descriptor", async () => {
    const host = await identity("updates");
    const { token } = await register(host, scenario.ip, { issuedAt: 1000 });
    const update = async (descriptor: string) =>
      call("PUT", `/v1/hosts/${host.serverId}/descriptor`, { ip: scenario.ip, token, body: { descriptor } });
    const hidden = await update(await signedDescriptor(host, { issuedAt: 1001, visibility: "invite" }));
    expect(hidden.json).toMatchObject({ listed: false });
    expect(await listedIds()).not.toContain(host.serverId);
    const moved = await update(await signedDescriptor(host, { issuedAt: 1002, gameBuild: "2546" }));
    expect(moved.json).toMatchObject({ listed: true });
    expect(await listedIds("build=2546&protocol=1")).toContain(host.serverId);
    expect(await listedIds()).not.toContain(host.serverId);
    expect((await update(await signedDescriptor(host, { issuedAt: 1001 }))).json["error"]).toMatchObject({ code: "stale-descriptor" });
    const foreign = await update(await signedDescriptor(await identity("foreign"), { issuedAt: 2000 }));
    expect(foreign.json["error"]).toMatchObject({ code: "invalid-descriptor" });
  });

  it("end a session on request", async () => {
    const host = await identity("ending");
    const { token, response } = await register(host, scenario.ip);
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    const ended = await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token });
    expect(ended.status).toBe(204);
    expect(ended.text).toBe("");
    expect(await listedIds()).not.toContain(host.serverId);
    expect((await call("POST", "/v1/invites/resolve", { ip: scenario.otherIp, body: { code } })).status).toBe(404);
    expect((await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token })).status).toBe(401);
  });

  it("replace the token on re-registration and keep the session's invites", async () => {
    const host = await identity("restart");
    const first = await register(host, scenario.ip);
    const code = (first.response.json["invite"] as Record<string, string>)["code"];
    const second = await register(host, scenario.ip);
    expect((second.response.json["invite"] as Record<string, string>)["code"]).toBe(code);
    expect((await call("GET", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token: first.token })).status).toBe(401);
    const view = await call("GET", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token: second.token });
    expect(view.status).toBe(200);
    expect((view.json["invites"] as Record<string, string>[]).map((invite) => invite["code"])).toEqual([code]);
  });

  it("start afresh when an expired session was never swept, so old invites do not carry over", async () => {
    const host = await identity("unswept");
    const first = await register(host, scenario.ip);
    const oldCode = (first.response.json["invite"] as Record<string, string>)["code"]!;
    scenario.clock.advance(200);
    const second = await register(host, scenario.ip);
    expect((second.response.json["invite"] as Record<string, string>)["code"]).not.toBe(oldCode);
    expect((await call("POST", "/v1/invites/resolve", { ip: scenario.otherIp, body: { code: oldCode } })).status).toBe(404);
  });
});
