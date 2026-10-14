import { runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { ROUTES } from "../src/index";
import { inviteShardName } from "../src/objects/invite-shard";
import { serviceKeys } from "../src/shared/keys";
import { readJoinTicket } from "../src/shared/tokens";
import { call, identity, register, type ApiResult } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("invites");
});
afterEach(() => endScenario());

const seconds = () => Math.floor(scenario.clock.now() / 1000);
const resolve = (code: string, ip = scenario.otherIp) => call("POST", "/v1/invites/resolve", { ip, body: { code } });
const createInvite = (serverId: string, token: string, ttlSeconds = 3600) =>
  call("POST", `/v1/hosts/${serverId}/invites`, { ip: scenario.ip, token, body: { ttlSeconds } });
const revokeInvite = (serverId: string, token: string, code: string) =>
  call("POST", `/v1/hosts/${serverId}/invites/revoke`, { ip: scenario.ip, token, body: { code } });
const statusCounts = (results: readonly ApiResult[]) =>
  results.reduce<Record<number, number>>((counts, result) => ({ ...counts, [result.status]: (counts[result.status] ?? 0) + 1 }), {});

/** Everything a client can see of a miss is identical, apart from the runtime's own Date header. */
function sameMiss(response: ApiResult, reference: ApiResult): void {
  const visible = (result: ApiResult) => [...result.headers].filter(([name]) => name !== "date");
  expect(response.status).toBe(404);
  expect(response.text).toBe(reference.text);
  expect(visible(response)).toEqual(visible(reference));
}

describe("invites", () => {
  it("are created, listed and revoked by their host, and resolve to the signed descriptor", async () => {
    const host = await identity("inviting");
    const { token } = await register(host, scenario.ip, { visibility: "invite" });
    const created = await createInvite(host.serverId, token, 600);
    expect(created.status).toBe(201);
    const invite = created.json["invite"] as Record<string, string | number>;
    expect(invite["expiresAt"]).toBe(seconds() + 600);
    const resolved = await resolve(invite["code"] as string);
    expect(resolved.status).toBe(200);
    expect(resolved.json).toMatchObject({ serverId: host.serverId, listing: { serverId: host.serverId, visibility: "invite" } });
    expect(resolved.json["descriptor"]).toEqual(expect.stringContaining(host.serverId));
    const view = await call("GET", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token });
    expect((view.json["invites"] as unknown[]).length).toBe(2);
    const withdrawn = await revokeInvite(host.serverId, token, invite["code"] as string);
    expect(withdrawn.status).toBe(204);
    expect(withdrawn.text).toBe("");
    expect((await resolve(invite["code"] as string)).status).toBe(404);
    expect((await revokeInvite(host.serverId, token, invite["code"] as string)).status).toBe(404);
    expect((await revokeInvite(host.serverId, token, "not a code")).status).toBe(404);
    expect((await revokeInvite(host.serverId, "tfh1_" + "A".repeat(43), invite["code"] as string)).status).toBe(401);
  });

  it("are withdrawn with the code in the body, never in a URL", async () => {
    const host = await identity("withdraw-by-body");
    const { token, response } = await register(host, scenario.ip);
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    // The old route, with the code as a path segment, no longer exists.
    expect((await call("DELETE", `/v1/hosts/${host.serverId}/invites/${code}`, { ip: scenario.ip, token })).status).toBe(404);
    expect((await resolve(code)).status).toBe(200);
    for (const route of ROUTES) expect(route.pattern.source, "no route takes an invite code").not.toContain("0-9A-Z");
    expect((await call("POST", `/v1/hosts/${host.serverId}/invites/revoke`, { ip: scenario.ip, token, body: { code, extra: true } })).status).toBe(400);
    expect((await revokeInvite(host.serverId, token, code)).status).toBe(204);
    expect((await resolve(code)).status).toBe(404);
  });

  it("stay live when the invite shard cannot be told, so withdrawing again works", async () => {
    const host = await identity("withdraw-shard-down");
    const { token } = await register(host, scenario.ip);
    const code = ((await createInvite(host.serverId, token)).json["invite"] as Record<string, string>)["code"]!;
    const session = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    const unreachable = () => ({ revoke: () => Promise.reject(new Error("invite shard unreachable")) });
    await runInDurableObject(session, (instance) => {
      (instance as unknown as Record<string, unknown>)["invites"] = unreachable;
    });
    try {
      expect((await revokeInvite(host.serverId, token, code)).status).toBe(500);
    } finally {
      await runInDurableObject(session, (instance) => {
        delete (instance as unknown as Record<string, unknown>)["invites"];
      });
    }
    // The withdrawal did not happen anywhere: the host still holds the invite, it still resolves, and trying again works.
    const view = await call("GET", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token });
    expect((view.json["invites"] as Record<string, string>[]).map((invite) => invite["code"])).toContain(code);
    expect((await resolve(code)).status).toBe(200);
    expect((await revokeInvite(host.serverId, token, code)).status).toBe(204);
    expect((await resolve(code)).status).toBe(404);
  });

  it("resolve only while the session itself still holds the code, whatever the shard says", async () => {
    const host = await identity("stale-shard");
    await register(host, scenario.ip);
    // A shard entry the session never issued, as a lost withdrawal would leave behind.
    const stale = "ZZZZZ00000";
    const shard = env.INVITE_SHARDS.get(env.INVITE_SHARDS.idFromName(inviteShardName(stale)));
    await runInDurableObject(shard, (instance) => instance.place(stale, host.serverId, seconds() + 3600));
    sameMiss(await resolve(stale, "192.0.2.30"), await resolve("ZZZZZZZZZZ", "192.0.2.31"));
  });

  it("read forgivingly: case, hyphens, spaces and look-alike letters", async () => {
    const host = await identity("forgiving");
    const { response } = await register(host, scenario.ip);
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    const spelled = `${code.slice(0, 5).toLowerCase()} - ${code.slice(5).replace(/0/g, "o").replace(/1/g, "l")}`;
    expect((await resolve(spelled)).json["serverId"]).toBe(host.serverId);
  });

  it("stop at eight per host and refuse lifetimes outside five minutes to seven days", async () => {
    const host = await identity("many-invites");
    const { token } = await register(host, scenario.ip);
    for (let index = 0; index < 7; index++) expect((await createInvite(host.serverId, token)).status).toBe(201);
    const ninth = await createInvite(host.serverId, token);
    expect(ninth.status).toBe(409);
    expect(ninth.json["error"]).toMatchObject({ code: "invite-limit" });
    expect((await createInvite(host.serverId, token, 299)).status).toBe(400);
    expect((await createInvite(host.serverId, token, 7 * 86400 + 1)).status).toBe(400);
    expect((await createInvite(host.serverId, "tfh1_" + "A".repeat(43))).status).toBe(401);
  });

  it("expire on time", async () => {
    const host = await identity("expiring");
    const { token } = await register(host, scenario.ip);
    const code = ((await createInvite(host.serverId, token, 300)).json["invite"] as Record<string, string>)["code"]!;
    for (let beat = 0; beat < 10; beat++) {
      scenario.clock.advance(30);
      await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 0 } });
    }
    expect((await resolve(code)).status).toBe(404);
  });

  it("answer every kind of miss with the identical 404", async () => {
    const host = await identity("uniform");
    const { token } = await register(host, scenario.ip);
    const revoked = ((await createInvite(host.serverId, token)).json["invite"] as Record<string, string>)["code"]!;
    await revokeInvite(host.serverId, token, revoked);
    const reference = await resolve("ZZZZZZZZZZ", "192.0.2.1");
    sameMiss(await resolve(revoked, "192.0.2.2"), reference);
    sameMiss(await resolve("not a code", "192.0.2.3"), reference);
    sameMiss(await resolve("ABCDEU2345", "192.0.2.4"), reference);
    const gone = await identity("uniform-gone");
    const ended = await register(gone, scenario.ip);
    const goneCode = (ended.response.json["invite"] as Record<string, string>)["code"]!;
    await call("DELETE", `/v1/hosts/${gone.serverId}`, { ip: scenario.ip, token: ended.token });
    sameMiss(await resolve(goneCode, "192.0.2.5"), reference);
    // A host whose heartbeats stopped, before its alarm has swept anything: the code still maps, the session is gone.
    const lapsed = await identity("uniform-lapsed");
    const lapsedCode = ((await register(lapsed, scenario.ip)).response.json["invite"] as Record<string, string>)["code"]!;
    scenario.clock.advance(91);
    sameMiss(await resolve(lapsedCode, "192.0.2.6"), reference);
    sameMiss(
      await call("POST", "/v1/join-tickets", { ip: "192.0.2.7", body: { serverId: lapsed.serverId, invite: lapsedCode, relayOnly: false } }),
      reference,
    );
    expect(reference.json["error"]).toEqual({ code: "not-found", message: "Not found." });
  });

  it("refuse a prefix that keeps missing, even for a code that exists", async () => {
    const host = await identity("guessed");
    const { response } = await register(host, scenario.ip);
    const real = (response.json["invite"] as Record<string, string>)["code"]!;
    for (let attempt = 0; attempt < 10; attempt++) expect((await resolve(`ZZZZZZZZ${String(attempt).padStart(2, "0")}`, "192.0.2.77")).status).toBe(404);
    const blocked = await resolve(real, "192.0.2.78");
    expect(blocked.status).toBe(429);
    expect(Number(blocked.headers.get("retry-after"))).toBeGreaterThan(0);
    expect((await resolve(real, "198.51.100.200")).status).toBe(200);
    // Two hours on, the prefix's misses have aged out: a miss is a plain 404 again, not a refusal.
    scenario.clock.advance(3600 * 2);
    expect((await resolve("ZZZZZZZZZZ", "192.0.2.79")).status).toBe(404);
  });

  it("count every miss of a concurrent burst, so wrong codes cannot outrun the limit", async () => {
    const burst = await Promise.all(Array.from({ length: 40 }, (_, index) => resolve(`ZZZZZZZZ${String(index).padStart(2, "0")}`, "192.0.2.60")));
    expect(statusCounts(burst)).toEqual({ 404: 10, 429: 30 });
    const host = await identity("burst-target");
    await register(host, scenario.ip, { visibility: "invite" });
    const tickets = await Promise.all(
      Array.from({ length: 40 }, (_, index) =>
        call("POST", "/v1/join-tickets", { ip: "198.51.100.60", body: { serverId: host.serverId, invite: `ZZZZZZZZ${String(index).padStart(2, "0")}`, relayOnly: false } }),
      ),
    );
    expect(statusCounts(tickets)).toEqual({ 404: 10, 429: 30 });
  });

  it("give a lookup's slot back when it hits, so finding codes never counts as missing", async () => {
    const host = await identity("hits");
    const { response } = await register(host, scenario.ip);
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    for (let attempt = 0; attempt < 15; attempt++) expect((await resolve(code, "192.0.2.70")).status).toBe(200);
    for (let attempt = 0; attempt < 15; attempt++) {
      const ticket = await call("POST", "/v1/join-tickets", { ip: "192.0.2.71", body: { serverId: host.serverId, invite: code, relayOnly: false } });
      expect(ticket.status).toBe(201);
    }
    // The prefix has missed nothing yet, so all ten misses it is allowed come before any refusal.
    for (let attempt = 0; attempt < 10; attempt++) expect((await resolve(`ZZZZZZZZ${String(attempt).padStart(2, "0")}`, "192.0.2.72")).status).toBe(404);
    expect((await resolve(code, "192.0.2.73")).status).toBe(429);
  });
});

describe("join tickets", () => {
  it("open public servers to anyone and carry the joiner's privacy choice", async () => {
    const host = await identity("public-tickets");
    await register(host, scenario.ip, { relayOnly: true });
    const response = await call("POST", "/v1/join-tickets", { ip: scenario.otherIp, body: { serverId: host.serverId, invite: null, relayOnly: true } });
    expect(response.status).toBe(201);
    expect(response.json).toMatchObject({ serverId: host.serverId, relayOnly: true, hostRelayOnly: true, expiresAt: seconds() + 6 * 3600 });
    const keys = (await serviceKeys("dGVzdC1vbmx5IHNlcnZpY2Uga2V5LCBub3QgcmVhbCE"))!;
    const ticket = await readJoinTicket(keys.ticket, response.json["ticket"], seconds());
    expect(ticket).toMatchObject({ serverId: host.serverId, relayOnly: true });
  });

  it("open invite-only servers only with one of their own invites, and count wrong ones as misses", async () => {
    const host = await identity("private-tickets");
    const { response } = await register(host, scenario.ip, { visibility: "invite" });
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    const ask = (invite: string | null, ip: string) => call("POST", "/v1/join-tickets", { ip, body: { serverId: host.serverId, invite, relayOnly: false } });
    expect((await ask(code.toLowerCase(), "192.0.2.10")).status).toBe(201);
    const reference = await resolve("ZZZZZZZZZZ", "192.0.2.11");
    sameMiss(await ask(null, "192.0.2.12"), reference);
    sameMiss(await ask("ZZZZZZZZZZ", "192.0.2.13"), reference);
    sameMiss(await ask("not a code", "192.0.2.14"), reference);
    const other = await identity("other-server");
    const otherCode = ((await register(other, scenario.ip)).response.json["invite"] as Record<string, string>)["code"]!;
    sameMiss(await ask(otherCode, "192.0.2.15"), reference);
    for (let attempt = 0; attempt < 10; attempt++) await ask("ZZZZZZZZZZ", "192.0.2.90");
    expect((await ask(code, "192.0.2.91")).status).toBe(429);
  });

  it("are refused for servers that do not exist", async () => {
    const response = await call("POST", "/v1/join-tickets", { ip: scenario.otherIp, body: { serverId: "f".repeat(64), invite: null, relayOnly: false } });
    expect(response.status).toBe(404);
    const malformed = await call("POST", "/v1/join-tickets", { ip: scenario.otherIp, body: { serverId: "F".repeat(64), invite: null, relayOnly: false } });
    expect(malformed.status).toBe(400);
  });
});
