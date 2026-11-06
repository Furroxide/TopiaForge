import { createExecutionContext, createScheduledController, runDurableObjectAlarm, runInDurableObject, waitOnExecutionContext } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import worker from "../src/index";
import { REVOCATIONS_PER_PASS, REVOCATION_RETRY_SECONDS } from "../src/objects/spend-guard";
import type { JsonObject } from "../src/shared/canonical-json";
import { call, identity, register, type ApiResult } from "./support/api";
import { useUpstream } from "./support/runtime";
import { endScenario, startScenario, type Scenario } from "./support/scenario";

const ADMIN_TOKEN = "test-only-admin-token-not-a-real-secret";
const ADMIN_IP = "192.0.2.250";
const JOINER_RESERVATION = 600 * 32 * 1024;

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("spend");
});
afterEach(async () => {
  await admin("PUT", "/v1/admin/kill-switch", { engaged: false, note: null });
  await admin("PUT", "/v1/admin/features", { hosting: true, invites: true, signaling: true, relay: true });
  endScenario();
});

const seconds = () => Math.floor(scenario.clock.now() / 1000);
const admin = (method: string, path: string, body?: JsonObject) =>
  call(method, path, { ip: ADMIN_IP, token: ADMIN_TOKEN, ...(body === undefined ? {} : { body }) });
const mint = (serverId: string, credential: string, ip: string): Promise<ApiResult> =>
  call("POST", "/v1/turn/credentials", { ip, token: credential, body: { serverId } });

async function host(label: string, maxPlayers = 16): Promise<{ readonly serverId: string; readonly token: string }> {
  const identityOfHost = await identity(`${label}-${scenario.day}`);
  const { token } = await register(identityOfHost, scenario.ip, { maxPlayers });
  return { serverId: identityOfHost.serverId, token };
}

async function ticket(serverId: string, ip = scenario.otherIp): Promise<string> {
  const response = await call("POST", "/v1/join-tickets", { ip, body: { serverId, invite: null, relayOnly: true } });
  expect(response.status).toBe(201);
  return response.json["ticket"] as string;
}

/** Moves time on while keeping the given sessions alive with heartbeats. */
async function keepAlive(sessions: readonly { readonly serverId: string; readonly token: string }[], totalSeconds: number): Promise<void> {
  // Even steps of at most 30 seconds keep every heartbeat inside the listing TTL and past the minimum gap.
  const steps = Math.ceil(totalSeconds / 30);
  for (let step = 0; step < steps; step++) {
    scenario.clock.advance(totalSeconds / steps);
    for (const session of sessions) {
      const beat = await call("POST", `/v1/hosts/${session.serverId}/heartbeat`, { ip: scenario.ip, token: session.token, body: { players: 1 } });
      expect(beat.status).toBe(200);
    }
  }
}

async function runCron(): Promise<void> {
  const context = createExecutionContext();
  await worker.scheduled(createScheduledController({ scheduledTime: scenario.clock.now(), cron: "7 * * * *" }), env, context);
  await waitOnExecutionContext(context);
}

const spendGuard = () => env.SPEND_GUARD.get(env.SPEND_GUARD.idFromName("spend-guard"));
const nextAlarm = () => runInDurableObject(spendGuard(), (_, state) => state.storage.getAlarm());
const spendState = async () => (await admin("GET", "/v1/admin/state")).json["spend"] as Record<string, number>;
/** The spend guard is one instance for every test; an alarm left from an earlier test's day would mask this test's. */
const runStaleAlarm = async () => void (await runDurableObjectAlarm(spendGuard()));

/** Starts a joiner's mint, with a fresh ticket unless one is given, and holds it at the provider until `release`. */
async function heldMint(serverId: string, ip: string, joiner?: string): Promise<{ readonly response: Promise<ApiResult>; readonly release: () => void }> {
  const credential = joiner ?? (await ticket(serverId));
  let open: () => void = () => undefined;
  scenario.relay.gate = new Promise((resolve) => (open = resolve));
  const arrivals = scenario.relay.generateBodies.length + 1;
  const response = mint(serverId, credential, ip);
  await scenario.relay.mintsArrived(arrivals);
  return {
    response,
    release: () => {
      scenario.relay.gate = null;
      open();
    },
  };
}

/** A mint that was in flight when relay was switched off: refused, its credential revoked, and nothing charged. */
async function expectWithdrawn(response: Promise<ApiResult>): Promise<void> {
  const answer = await response;
  expect(answer.status).toBe(503);
  expect(answer.json["error"]).toMatchObject({ code: "relay-unavailable" });
  expect(answer.text).not.toContain("test-credential-1");
  expect(scenario.relay.revoked).toEqual(["test-user-1"]);
  expect(await spendState()).toMatchObject({ mints: 0, reservedBytes: 0, activeCredentials: 0, pendingRevocations: 0 });
}

describe("relay credentials", () => {
  it("are minted for a host and for a joiner in the iceServers shape, through the provider's API", async () => {
    const session = await host("mint");
    const forHost = await mint(session.serverId, session.token, scenario.ip);
    expect(forHost.status).toBe(201);
    expect(forHost.json).toEqual({
      iceServers: [
        { urls: ["stun:stun.cloudflare.com:3478", "stun:stun.cloudflare.com:53"] },
        {
          urls: ["turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:3478?transport=tcp", "turns:turn.cloudflare.com:5349?transport=tcp"],
          username: "test-user-1",
          credential: "test-credential-1",
        },
      ],
      expiresAt: seconds() + 600,
      ttlSeconds: 600,
    });
    expect(scenario.relay.authorizations[0]).toBe("Bearer test-turn-api-token");
    expect(scenario.relay.generateBodies[0]).toEqual({ ttl: 600 });
    const forJoiner = await mint(session.serverId, await ticket(session.serverId), scenario.otherIp);
    expect(forJoiner.status).toBe(201);
    expect(forJoiner.headers.get("cache-control")).toBe("no-store");
  });

  it("keep only DNS-named provider URLs and refuse a provider answer they cannot trust", async () => {
    const session = await host("provider-shapes");
    const answers: [unknown, number][] = [
      [{ iceServers: [{ urls: ["turn:198.51.100.9:3478?transport=udp", "turn:turn.cloudflare.com:3478"], username: "u", credential: "c" }] }, 201],
      [{ iceServers: [{ urls: ["turn:198.51.100.9:3478?transport=udp"], username: "u", credential: "c" }] }, 502],
      [{ iceServers: [{ urls: ["turn:turn.cloudflare.com:3478"] }] }, 502],
      [{ iceServers: "nope" }, 502],
    ];
    for (const [answer, status] of answers) {
      useUpstream(() => Response.json(answer, { status: 201 }));
      const response = await mint(session.serverId, session.token, scenario.ip);
      expect(response.status).toBe(status);
      expect(response.text).not.toContain("198.51.100.9");
    }
  });

  it("are limited to five a minute and twenty an hour per address prefix", async () => {
    const session = await host("prefix-limits", 2);
    const tickets = await Promise.all(Array.from({ length: 22 }, (_, index) => ticket(session.serverId, `10.10.${index}.1`)));
    let used = 0;
    for (let minute = 0; minute < 8; minute += 2) {
      for (let index = 0; index < 5; index++) expect((await mint(session.serverId, tickets[used++]!, "198.51.100.10")).status).toBe(201);
      if (minute === 0) {
        const burst = await mint(session.serverId, tickets[used]!, "198.51.100.11");
        expect(burst.status).toBe(429);
        expect(Number(burst.headers.get("retry-after"))).toBeLessThanOrEqual(60);
      }
      await keepAlive([session], 120);
    }
    const hourly = await mint(session.serverId, tickets[used]!, "198.51.100.12");
    expect(hourly.status).toBe(429);
    expect(Number(hourly.headers.get("retry-after"))).toBeGreaterThan(60);
    expect((await mint(session.serverId, tickets[used]!, "203.0.113.200")).status).toBe(201);
  });

  it("are capped per ticket by count and by planned relay volume", async () => {
    const heavy = await host("ticket-bytes");
    for (let index = 0; index < 3; index++) expect((await mint(heavy.serverId, heavy.token, `10.1.${index}.1`)).status).toBe(201);
    const over = await mint(heavy.serverId, heavy.token, "10.1.9.1");
    expect(over.status).toBe(403);
    expect(over.json["error"]).toMatchObject({ code: "ticket-limit" });
    const light = await host("ticket-count", 2);
    const joiner = await ticket(light.serverId);
    for (let index = 0; index < 36; index++) expect((await mint(light.serverId, joiner, `10.2.${index}.1`)).status).toBe(201);
    expect((await mint(light.serverId, joiner, "10.2.99.1")).json["error"]).toMatchObject({ code: "ticket-limit" });
  });

  it("keep the day under its cap, counting every reservation the measurement has not yet seen", async () => {
    const first = await host("daily-a");
    const second = await host("daily-b");
    const third = await host("daily-c");
    for (const session of [first, second]) {
      for (let index = 0; index < 3; index++) expect((await mint(session.serverId, session.token, `10.3.${index}.${session === first ? 1 : 2}`)).status).toBe(201);
    }
    const capped = await mint(third.serverId, third.token, "10.3.9.1");
    expect(capped.status).toBe(503);
    expect(capped.json["error"]).toMatchObject({ code: "relay-unavailable" });
    // Once the credentials have expired and a measurement has seen what they really relayed, the budget frees up.
    await keepAlive([third], 600 + 15 * 60 + 1);
    scenario.relay.egressBytes = 100_000_000;
    await runCron();
    expect((await mint(third.serverId, third.token, "10.3.9.2")).status).toBe(201);
  });

  it("reserve before waiting on the provider, so concurrent mints never overshoot a limit", async () => {
    const session = await host("concurrent", 2);
    const tickets = await Promise.all(Array.from({ length: 8 }, (_, index) => ticket(session.serverId, `192.0.2.${100 + index}`)));
    let release: () => void = () => undefined;
    scenario.relay.gate = new Promise((resolve) => (release = resolve));
    const inFlight = tickets.map((joiner) => mint(session.serverId, joiner, "198.51.100.30"));
    await scenario.relay.mintsArrived(5);
    release();
    const statuses = (await Promise.all(inFlight)).map((response) => response.status).sort();
    expect(statuses).toEqual([201, 201, 201, 201, 201, 429, 429, 429]);
    scenario.relay.gate = null;

    const heavy = await host("concurrent-heavy");
    scenario.relay.gate = new Promise((resolve) => (release = resolve));
    const sameTicket = Array.from({ length: 4 }, (_, index) => mint(heavy.serverId, heavy.token, `10.4.${index}.1`));
    await scenario.relay.mintsArrived(8);
    release();
    expect((await Promise.all(sameTicket)).map((response) => response.status).sort()).toEqual([201, 201, 201, 403]);
  });

  it("refund everything when the provider fails", async () => {
    const session = await host("refund", 2);
    const joiner = await ticket(session.serverId);
    scenario.relay.failGenerate = true;
    const failed = await mint(session.serverId, joiner, "198.51.100.40");
    expect(failed.status).toBe(502);
    expect(failed.json["error"]).toMatchObject({ code: "upstream-failed" });
    scenario.relay.failGenerate = false;
    for (let index = 0; index < 5; index++) expect((await mint(session.serverId, joiner, "198.51.100.40")).status).toBe(201);
    expect((await mint(session.serverId, joiner, "198.51.100.40")).status).toBe(429);
    const state = await admin("GET", "/v1/admin/state");
    expect((state.json["spend"] as Record<string, number>)["reservedBytes"]).toBe(5 * JOINER_RESERVATION);
  });

  it("are refused, and revoked unseen, when an operator turns relay off while the provider is answering", async () => {
    const session = await host("in-flight-feature", 2);
    const held = await heldMint(session.serverId, "10.8.3.1");
    await admin("PUT", "/v1/admin/features", { relay: false });
    held.release();
    await expectWithdrawn(held.response);
  });

  it("are refused, and revoked unseen, even when relay is turned back on before the provider answers", async () => {
    const session = await host("in-flight-toggle", 2);
    const held = await heldMint(session.serverId, "10.8.4.1");
    await admin("PUT", "/v1/admin/features", { relay: false });
    await admin("PUT", "/v1/admin/features", { relay: true });
    held.release();
    await expectWithdrawn(held.response);
  });

  it("are refused, and revoked unseen, when a measurement blocks their ticket while the provider is answering", async () => {
    const session = await host("overuse-in-flight", 2);
    const greedy = await ticket(session.serverId);
    expect((await mint(session.serverId, greedy, "10.7.1.1")).status).toBe(201);
    scenario.relay.egressBytes = 3 * JOINER_RESERVATION;
    scenario.relay.byUsername.set("test-user-1", 2 * JOINER_RESERVATION + 1);
    const held = await heldMint(session.serverId, "10.7.1.2", greedy);
    // The measurement revokes the credential relaying past its plan and blocks its ticket, which the next mint meets.
    expect((await admin("POST", "/v1/admin/spend/measure")).status).toBe(200);
    expect(scenario.relay.revoked).toEqual(["test-user-1"]);
    expect((await mint(session.serverId, greedy, "10.7.1.3")).json["error"]).toMatchObject({ code: "ticket-limit" });
    held.release();
    const answer = await held.response;
    expect(answer.status).toBe(403);
    expect(answer.json["error"]).toMatchObject({ code: "ticket-limit" });
    expect(answer.text).not.toContain("test-credential-2");
    expect(scenario.relay.revoked).toEqual(["test-user-1", "test-user-2"]);
    // Only the credential that was handed out stays charged, and nothing is left live.
    expect(await spendState()).toMatchObject({ mints: 1, reservedBytes: JOINER_RESERVATION, activeCredentials: 0, pendingRevocations: 0 });
  });

  it("are refused when relay is off and for a session that has ended", async () => {
    const session = await host("gone", 2);
    const joiner = await ticket(session.serverId);
    await admin("PUT", "/v1/admin/features", { relay: false });
    expect((await mint(session.serverId, joiner, scenario.otherIp)).json["error"]).toMatchObject({ code: "relay-unavailable" });
    await admin("PUT", "/v1/admin/features", { relay: true });
    await call("DELETE", `/v1/hosts/${session.serverId}`, { ip: scenario.ip, token: session.token });
    expect((await mint(session.serverId, joiner, scenario.otherIp)).status).toBe(404);
    expect((await mint(session.serverId, session.token, scenario.ip)).status).toBe(401);
    expect((await mint("f".repeat(64), joiner, scenario.otherIp)).status).toBe(401);
  });
});

describe("the kill switch", () => {
  it("stops every mint and revokes the credentials already issued, until it is released", async () => {
    const session = await host("kill", 2);
    for (let index = 0; index < 2; index++) await mint(session.serverId, await ticket(session.serverId), `10.5.${index}.1`);
    const engaged = await admin("PUT", "/v1/admin/kill-switch", { engaged: true, note: "drill" });
    expect(engaged.json["killSwitch"]).toMatchObject({ engaged: true, reason: "manual", note: "drill" });
    expect(scenario.relay.revoked.sort()).toEqual(["test-user-1", "test-user-2"]);
    const refused = await mint(session.serverId, await ticket(session.serverId), "10.5.9.1");
    expect(refused.json["error"]).toMatchObject({ code: "relay-unavailable" });
    // Refused before the provider is asked, so nothing is minted only to be revoked.
    expect(scenario.relay.generateBodies).toHaveLength(2);
    const status = await call("GET", "/v1/status", { ip: scenario.otherIp });
    expect(status.json).toMatchObject({ killSwitch: true, features: { relay: false } });
    await admin("PUT", "/v1/admin/kill-switch", { engaged: false, note: null });
    expect((await mint(session.serverId, await ticket(session.serverId), "10.5.9.2")).status).toBe(201);
  });

  it("trips on the hourly measurement once the day's real egress reaches the cap", async () => {
    const session = await host("cron-trip", 2);
    await mint(session.serverId, await ticket(session.serverId), "10.6.0.1");
    scenario.relay.egressBytes = 2_000_000_000;
    await runCron();
    const state = await admin("GET", "/v1/admin/state");
    expect(state.json["killSwitch"]).toMatchObject({ engaged: true, reason: "spend-cap" });
    expect((state.json["spend"] as Record<string, number>)["measuredBytes"]).toBe(2_000_000_000);
    expect(scenario.relay.revoked).toEqual(["test-user-1"]);
    expect((await mint(session.serverId, await ticket(session.serverId), "10.6.0.2")).status).toBe(503);
  });

  it("trips after three hourly measurements in a row fail, and a success clears the count", async () => {
    scenario.relay.failUsage = true;
    await runCron();
    await runCron();
    expect(((await admin("GET", "/v1/admin/state")).json["spend"] as Record<string, number>)["measurementFailures"]).toBe(2);
    scenario.relay.failUsage = false;
    await runCron();
    expect(((await admin("GET", "/v1/admin/state")).json["spend"] as Record<string, number>)["measurementFailures"]).toBe(0);
    scenario.relay.failUsage = true;
    for (let run = 0; run < 3; run++) await runCron();
    expect((await admin("GET", "/v1/admin/state")).json["killSwitch"]).toMatchObject({ engaged: true, reason: "measurement-failed" });
  });

  it("revokes a credential whose mint was waiting on the provider when it engaged", async () => {
    const session = await host("in-flight-manual", 2);
    const held = await heldMint(session.serverId, "10.8.0.1");
    expect((await admin("PUT", "/v1/admin/kill-switch", { engaged: true, note: "drill" })).status).toBe(200);
    held.release();
    await expectWithdrawn(held.response);
  });

  it("revokes such a credential when the hourly measurement trips it, too", async () => {
    const session = await host("in-flight-cron", 2);
    const held = await heldMint(session.serverId, "10.8.1.1");
    scenario.relay.egressBytes = 2_000_000_000;
    await runCron();
    expect((await admin("GET", "/v1/admin/state")).json["killSwitch"]).toMatchObject({ engaged: true, reason: "spend-cap" });
    held.release();
    await expectWithdrawn(held.response);
  });

  it("revokes such a credential even when it was released again before the provider answered", async () => {
    const session = await host("in-flight-released", 2);
    const held = await heldMint(session.serverId, "10.8.2.1");
    await admin("PUT", "/v1/admin/kill-switch", { engaged: true, note: null });
    await admin("PUT", "/v1/admin/kill-switch", { engaged: false, note: null });
    held.release();
    await expectWithdrawn(held.response);
  });

  it("revokes every live credential promptly, a pass at a time, however many there are", async () => {
    await runStaleAlarm();
    const session = await host("many-live", 2);
    // More than one pass's worth, minted through two tickets and five to a prefix, as the mint limits allow.
    const live = REVOCATIONS_PER_PASS + 15;
    const tickets = [await ticket(session.serverId), await ticket(session.serverId)];
    for (let index = 0; index < live; index++) {
      expect((await mint(session.serverId, tickets[index < 30 ? 0 : 1]!, `10.9.${Math.floor(index / 5)}.1`)).status).toBe(201);
    }
    await admin("PUT", "/v1/admin/kill-switch", { engaged: true, note: null });
    // The engaging request runs the first pass itself, and the next is due at once rather than a minute later.
    expect(scenario.relay.revoked).toHaveLength(REVOCATIONS_PER_PASS);
    expect(await nextAlarm()).toBe(scenario.clock.now());
    expect(await runDurableObjectAlarm(spendGuard())).toBe(true);
    expect(new Set(scenario.relay.revoked).size).toBe(live);
    expect(await spendState()).toMatchObject({ activeCredentials: 0, pendingRevocations: 0 });
  });

  it("waits a minute before trying again when the provider refuses every revocation in a pass", async () => {
    await runStaleAlarm();
    const session = await host("revoke-retry", 2);
    expect((await mint(session.serverId, await ticket(session.serverId), "10.9.20.1")).status).toBe(201);
    scenario.relay.failRevoke = true;
    await admin("PUT", "/v1/admin/kill-switch", { engaged: true, note: null });
    expect(scenario.relay.revoked).toEqual([]);
    expect(await spendState()).toMatchObject({ activeCredentials: 1, pendingRevocations: 1 });
    expect(await nextAlarm()).toBe(scenario.clock.now() + REVOCATION_RETRY_SECONDS * 1000);
    scenario.relay.failRevoke = false;
    expect(await runDurableObjectAlarm(spendGuard())).toBe(true);
    expect(scenario.relay.revoked).toEqual(["test-user-1"]);
    expect(await spendState()).toMatchObject({ activeCredentials: 0, pendingRevocations: 0 });
  });

  it("is left alone by a measurement under the cap, which instead revokes a credential relaying far past its plan", async () => {
    const session = await host("overuse", 2);
    const greedy = await ticket(session.serverId);
    const modest = await ticket(session.serverId);
    await mint(session.serverId, greedy, "10.7.0.1");
    await mint(session.serverId, modest, "10.7.0.2");
    scenario.relay.egressBytes = 3 * JOINER_RESERVATION;
    scenario.relay.byUsername.set("test-user-1", 2 * JOINER_RESERVATION + 1);
    scenario.relay.byUsername.set("test-user-2", JOINER_RESERVATION);
    await runCron();
    expect((await admin("GET", "/v1/admin/state")).json["killSwitch"]).toMatchObject({ engaged: false });
    expect(scenario.relay.revoked).toEqual(["test-user-1"]);
    expect((await mint(session.serverId, greedy, "10.7.0.3")).json["error"]).toMatchObject({ code: "ticket-limit" });
    expect((await mint(session.serverId, modest, "10.7.0.4")).status).toBe(201);
  });
});
