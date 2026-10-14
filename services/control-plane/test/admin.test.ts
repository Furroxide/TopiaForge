import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { JsonObject } from "../src/shared/canonical-json";
import { call, identity, register, registrationBody, registrationProof, signedDescriptor } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";
import { openSignal } from "./support/sockets";

const ADMIN_TOKEN = "test-only-admin-token-not-a-real-secret";

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("admin");
});
afterEach(async () => {
  await admin("PUT", "/v1/admin/kill-switch", { engaged: false, note: null });
  await admin("PUT", "/v1/admin/features", { hosting: true, invites: true, signaling: true, relay: true });
  endScenario();
});

const seconds = () => Math.floor(scenario.clock.now() / 1000);
const admin = (method: string, path: string, body?: JsonObject | string, token = ADMIN_TOKEN) =>
  call(method, path, { ip: "192.0.2.240", token, ...(body === undefined ? {} : { body }) });

describe("admin access", () => {
  it("refuses every request without the admin token with the same 401", async () => {
    const reference = await call("GET", "/v1/admin/state", { ip: "192.0.2.241" });
    expect(reference.status).toBe(401);
    expect(reference.json).toEqual({ error: { code: "unauthorized", message: "Valid credentials are required." } });
    for (const headers of [{ authorization: `Basic ${ADMIN_TOKEN}` }, { authorization: `Bearer ${ADMIN_TOKEN}x` }, { authorization: "Bearer " }, { authorization: `Bearer ${ADMIN_TOKEN.slice(0, -1)}` }]) {
      const response = await call("GET", "/v1/admin/state", { ip: "192.0.2.241", headers });
      expect(response.status).toBe(401);
      expect(response.text).toBe(reference.text);
    }
    expect((await admin("GET", "/v1/admin/state")).status).toBe(200);
  });

  it("limits how often a prefix may try", async () => {
    for (let attempt = 0; attempt < 30; attempt++) await call("GET", "/v1/admin/state", { ip: "192.0.2.242", token: "wrong-wrong-wrong-wrong-wrong-wrong" });
    expect((await call("GET", "/v1/admin/state", { ip: "192.0.2.243", token: ADMIN_TOKEN })).status).toBe(429);
  });
});

describe("admin controls", () => {
  it("report the service's state, spend and limits", async () => {
    const state = await admin("GET", "/v1/admin/state");
    expect(state.json).toMatchObject({
      environment: "test",
      features: { hosting: true, invites: true, signaling: true, relay: true },
      killSwitch: { engaged: false, reason: null, since: null, note: null },
      spend: { mints: 0, reservedBytes: 0, dailyCapBytes: 2_000_000_000, ticketCapBytes: 1_000_000_000, providerConfigured: true, analyticsConfigured: true },
      limits: { credentialTtlSeconds: 600, ipMintsPerMinute: 5, ipMintsPerHour: 20, ticketMaxMints: 36, measurementFailureLimit: 3 },
    });
  });

  it("switch features off for every client, and back on", async () => {
    const host = await identity(`flags-${scenario.day}`);
    const { token, response } = await register(host, scenario.ip, { visibility: "invite" });
    const code = (response.json["invite"] as Record<string, string>)["code"]!;
    await admin("PUT", "/v1/admin/features", { hosting: false, invites: false, signaling: false });
    const status = await call("GET", "/v1/status", { ip: scenario.otherIp });
    expect(status.json["features"]).toEqual({ hosting: false, invites: false, signaling: false, relay: true });
    const challenge = await call("POST", "/v1/hosts/challenge", { ip: scenario.ip, body: { signingKey: host.signingKey } });
    expect(challenge.json["error"]).toMatchObject({ code: "feature-disabled" });
    expect((await call("POST", "/v1/invites/resolve", { ip: scenario.otherIp, body: { code } })).status).toBe(503);
    expect((await call("POST", `/v1/hosts/${host.serverId}/invites`, { ip: scenario.ip, token, body: { ttlSeconds: 600 } })).status).toBe(503);
    expect((await openSignal(host.serverId, token, scenario.ip)).status).toBe(503);
    // A running session carries on: heartbeats are not new hosting.
    scenario.clock.advance(30);
    expect((await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 1 } })).status).toBe(200);
    await admin("PUT", "/v1/admin/features", { hosting: true, invites: true, signaling: true });
    expect((await call("POST", "/v1/invites/resolve", { ip: scenario.otherIp, body: { code } })).status).toBe(200);
  });

  it("delist a server and refuse its registration until the hold ends", async () => {
    const host = await identity(`delisted-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    expect((await admin("POST", `/v1/admin/servers/${host.serverId}/delist`, { holdSeconds: 3600 })).status).toBe(204);
    const listed = await call("GET", "/v1/servers?build=2545&protocol=1", { ip: scenario.otherIp });
    expect((listed.json["servers"] as Record<string, string>[]).map((server) => server["serverId"])).not.toContain(host.serverId);
    expect((await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 0 } })).status).toBe(401);
    const issued = await call("POST", "/v1/hosts/challenge", { ip: scenario.ip, body: { signingKey: host.signingKey } });
    const body = registrationBody(issued.json["challenge"] as string, await signedDescriptor(host), false);
    const held = await call("POST", "/v1/hosts", { ip: scenario.ip, body: { ...body, proof: await registrationProof(host, body) } });
    expect(held.status).toBe(403);
    expect(held.json["error"]).toMatchObject({ code: "server-blocked" });
    scenario.clock.advance(3601);
    expect((await register(host, scenario.otherIp)).response.status).toBe(201);
  });

  it("measure spend on demand", async () => {
    scenario.relay.egressBytes = 12345;
    const measured = await admin("POST", "/v1/admin/spend/measure");
    expect(measured.status).toBe(200);
    expect(measured.json["spend"]).toMatchObject({ measuredBytes: 12345, measuredAt: seconds(), measurementFailures: 0 });
  });

  it("validate every admin request", async () => {
    for (const [method, path, body] of [
      ["PUT", "/v1/admin/kill-switch", { engaged: true }],
      ["PUT", "/v1/admin/kill-switch", { engaged: "yes", note: null }],
      ["PUT", "/v1/admin/kill-switch", { engaged: true, note: "line\nbreak" }],
      ["PUT", "/v1/admin/features", {}],
      ["PUT", "/v1/admin/features", { voice: false }],
      ["PUT", "/v1/admin/features", { relay: "off" }],
      ["POST", `/v1/admin/servers/${"a".repeat(64)}/delist`, { holdSeconds: 30 * 86400 + 1 }],
      ["POST", "/v1/admin/spend/measure", "{}"],
    ] as const) {
      expect((await admin(method, path, body as JsonObject | string)).status, `${method} ${path}`).toBe(400);
    }
    const wrongMethod = await admin("POST", "/v1/admin/state");
    expect(wrongMethod.status).toBe(405);
    expect(wrongMethod.headers.get("allow")).toBe("GET");
    expect((await admin("GET", "/v1/admin/state?verbose=1")).status).toBe(400);
  });
});
