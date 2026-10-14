/**
 * Join tickets hold only while the admission they were issued under does: the same host session, and the invite they
 * were issued with still live, or, for a ticket issued without one, a session that is still public. Signaling
 * upgrades and relay credentials both check, and the room closes the socket of a ticket that stops holding. A mint
 * already waiting on the relay provider checks again once the provider answers.
 */
import { runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { HostSession } from "../src/objects/host-session";
import { serviceKeys } from "../src/shared/keys";
import { issueJoinTicket, readJoinTicket } from "../src/shared/tokens";
import { call, identity, register, signedDescriptor, type ApiResult } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";
import { openSignal, type SignalClient, type Upgrade } from "./support/sockets";

let scenario: Scenario;
const open: SignalClient[] = [];
beforeEach(() => {
  scenario = startScenario("ticket scope");
});
afterEach(() => {
  for (const client of open.splice(0)) client.close();
  endScenario();
});

const PEER_IP = "198.51.100.77";
const ADMIN_TOKEN = "test-only-admin-token-not-a-real-secret";

async function ticketFor(serverId: string, invite: string | null, relayOnly = false): Promise<string> {
  const response = await call("POST", "/v1/join-tickets", { ip: PEER_IP, body: { serverId, invite, relayOnly } });
  expect(response.status).toBe(201);
  return response.json["ticket"] as string;
}

async function signal(serverId: string, ticket: string): Promise<Upgrade> {
  const upgrade = await openSignal(serverId, ticket, PEER_IP);
  if (upgrade.client !== null) open.push(upgrade.client);
  return upgrade;
}

const mint = (serverId: string, ticket: string): Promise<ApiResult> => call("POST", "/v1/turn/credentials", { ip: PEER_IP, token: ticket, body: { serverId } });
const seconds = (): number => Math.floor(scenario.clock.now() / 1000);
const inviteCode = (result: ApiResult): string => (result.json["invite"] as Record<string, string>)["code"]!;
const spendState = async () => (await call("GET", "/v1/admin/state", { ip: "192.0.2.250", token: ADMIN_TOKEN })).json["spend"] as Record<string, number>;

/** Holds every mint at the relay provider until the returned function is called. */
function holdProvider(): () => void {
  let letThrough: () => void = () => undefined;
  scenario.relay.gate = new Promise((resolve) => (letThrough = resolve));
  return () => {
    scenario.relay.gate = null;
    letThrough();
  };
}

/** A mint that was waiting on the provider when its admission lapsed: refused, with no credential in the answer. */
async function expectRefusedUnseen(response: Promise<ApiResult>, status: number, code: string): Promise<void> {
  const answer = await response;
  expect(answer.status).toBe(status);
  expect(answer.json["error"]).toMatchObject({ code });
  expect(answer.text).not.toContain("test-credential");
}

/** Every credential the provider minted is revoked, and nothing stays charged or live. */
async function expectNothingLive(minted: number): Promise<void> {
  expect(scenario.relay.revoked.sort()).toEqual(Array.from({ length: minted }, (_, index) => `test-user-${index + 1}`));
  expect(await spendState()).toMatchObject({ mints: 0, reservedBytes: 0, activeCredentials: 0, pendingRevocations: 0 });
}

describe("a join ticket", () => {
  it("stops opening signaling and minting relay once its invite is withdrawn, and its socket closes", async () => {
    const host = await identity(`withdrawn-${scenario.day}`);
    const { token, response } = await register(host, scenario.ip, { visibility: "invite" });
    const extra = inviteCode(await call("POST", `/v1/hosts/${host.serverId}/invites`, { ip: scenario.ip, token, body: { ttlSeconds: 3600 } }));
    const withdrawnTicket = await ticketFor(host.serverId, extra);
    const keptTicket = await ticketFor(host.serverId, inviteCode(response));
    const connected = await signal(host.serverId, withdrawnTicket);
    expect(connected.status).toBe(101);
    await connected.client!.next();
    expect((await mint(host.serverId, withdrawnTicket)).status).toBe(201);
    expect((await call("POST", `/v1/hosts/${host.serverId}/invites/revoke`, { ip: scenario.ip, token, body: { code: extra } })).status).toBe(204);
    expect((await connected.client!.closed()).code).toBe(4403);
    expect((await signal(host.serverId, withdrawnTicket)).status).toBe(401);
    const refused = await mint(host.serverId, withdrawnTicket);
    expect(refused.status).toBe(401);
    expect(refused.json["error"]).toMatchObject({ code: "unauthorized" });
    // A ticket issued under another invite is untouched.
    expect((await signal(host.serverId, keptTicket)).status).toBe(101);
    expect((await mint(host.serverId, keptTicket)).status).toBe(201);
  });

  it("belongs to one host session: a host that ends it and registers again starts with no tickets", async () => {
    const host = await identity(`epoch-${scenario.day}`);
    const first = await register(host, scenario.ip);
    const ticket = await ticketFor(host.serverId, null);
    expect((await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token: first.token })).status).toBe(204);
    await register(host, scenario.ip);
    expect((await signal(host.serverId, ticket)).status).toBe(401);
    expect((await mint(host.serverId, ticket)).status).toBe(401);
    expect((await signal(host.serverId, await ticketFor(host.serverId, null))).status).toBe(101);
  });

  it("survives the host registering again while its session is live", async () => {
    const host = await identity(`restart-${scenario.day}`);
    await register(host, scenario.ip);
    const ticket = await ticketFor(host.serverId, null);
    await register(host, scenario.ip);
    expect((await signal(host.serverId, ticket)).status).toBe(101);
    expect((await mint(host.serverId, ticket)).status).toBe(201);
  });

  it("issued without an invite stops holding once the session stops being public", async () => {
    const host = await identity(`gone-private-${scenario.day}`);
    const { token, response } = await register(host, scenario.ip, { issuedAt: 1000 });
    const withoutInvite = await ticketFor(host.serverId, null);
    const withInvite = await ticketFor(host.serverId, inviteCode(response));
    const connected = await signal(host.serverId, withoutInvite);
    await connected.client!.next();
    const descriptor = await signedDescriptor(host, { issuedAt: 1001, visibility: "invite" });
    expect((await call("PUT", `/v1/hosts/${host.serverId}/descriptor`, { ip: scenario.ip, token, body: { descriptor } })).status).toBe(200);
    expect((await connected.client!.closed()).code).toBe(4403);
    expect((await signal(host.serverId, withoutInvite)).status).toBe(401);
    expect((await mint(host.serverId, withoutInvite)).status).toBe(401);
    expect((await signal(host.serverId, withInvite)).status).toBe(101);
  });

  it("stops holding when its invite expires, and its socket closes then", async () => {
    const host = await identity(`expired-invite-${scenario.day}`);
    const { token } = await register(host, scenario.ip, { visibility: "invite" });
    const created = await call("POST", `/v1/hosts/${host.serverId}/invites`, { ip: scenario.ip, token, body: { ttlSeconds: 300 } });
    const expiresAt = (created.json["invite"] as Record<string, number>)["expiresAt"]!;
    const ticket = await ticketFor(host.serverId, inviteCode(created));
    const connected = await signal(host.serverId, ticket);
    await connected.client!.next();
    const session = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    const beat = async (count: number) => {
      for (let index = 0; index < count; index++) {
        scenario.clock.advance(30);
        expect((await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 1 } })).status).toBe(200);
      }
    };
    await beat(8);
    // One sweep clears the registration's spent challenge. Heartbeats keep moving the session's own expiry on, so what
    // the next alarm waits for is the invite's expiry.
    expect(await runDurableObjectAlarm(session)).toBe(true);
    expect(connected.client!.pending()).toEqual([]);
    expect(await runInDurableObject(session, (_, state) => state.storage.getAlarm())).toBe(expiresAt * 1000);
    await beat(2);
    expect(await runDurableObjectAlarm(session)).toBe(true);
    expect((await connected.client!.closed()).code).toBe(4403);
    expect((await signal(host.serverId, ticket)).status).toBe(401);
  });
});

describe("a mint waiting on the relay provider", () => {
  it("gets no credential once its ticket's invite is withdrawn", async () => {
    const host = await identity(`withdrawn-in-flight-${scenario.day}`);
    const { token } = await register(host, scenario.ip, { visibility: "invite" });
    const code = inviteCode(await call("POST", `/v1/hosts/${host.serverId}/invites`, { ip: scenario.ip, token, body: { ttlSeconds: 3600 } }));
    const ticket = await ticketFor(host.serverId, code, true);
    const release = holdProvider();
    const held = mint(host.serverId, ticket);
    await scenario.relay.mintsArrived(1);
    expect((await call("POST", `/v1/hosts/${host.serverId}/invites/revoke`, { ip: scenario.ip, token, body: { code } })).status).toBe(204);
    expect((await mint(host.serverId, ticket)).status).toBe(401);
    release();
    await expectRefusedUnseen(held, 401, "unauthorized");
    await expectNothingLive(1);
  });

  it("gets no credential once the host ends its session and registers again", async () => {
    const host = await identity(`epoch-in-flight-${scenario.day}`);
    const first = await register(host, scenario.ip);
    const ticket = await ticketFor(host.serverId, null, true);
    const release = holdProvider();
    const held = mint(host.serverId, ticket);
    await scenario.relay.mintsArrived(1);
    expect((await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token: first.token })).status).toBe(204);
    await register(host, scenario.ip);
    release();
    await expectRefusedUnseen(held, 401, "unauthorized");
    await expectNothingLive(1);
  });

  it("gets no credential once the session stops being public, for a ticket issued without an invite", async () => {
    const host = await identity(`private-in-flight-${scenario.day}`);
    const { token } = await register(host, scenario.ip, { issuedAt: 1000 });
    const ticket = await ticketFor(host.serverId, null, true);
    const release = holdProvider();
    const held = mint(host.serverId, ticket);
    await scenario.relay.mintsArrived(1);
    const descriptor = await signedDescriptor(host, { issuedAt: 1001, visibility: "invite" });
    expect((await call("PUT", `/v1/hosts/${host.serverId}/descriptor`, { ip: scenario.ip, token, body: { descriptor } })).status).toBe(200);
    release();
    await expectRefusedUnseen(held, 401, "unauthorized");
    await expectNothingLive(1);
  });

  it("gets no credential on either side once the session ends, answered as a request made now would be", async () => {
    const host = await identity(`ended-in-flight-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const ticket = await ticketFor(host.serverId, null, true);
    const release = holdProvider();
    const joiner = mint(host.serverId, ticket);
    const own = call("POST", "/v1/turn/credentials", { ip: scenario.ip, token, body: { serverId: host.serverId } });
    await scenario.relay.mintsArrived(2);
    expect((await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token })).status).toBe(204);
    release();
    await expectRefusedUnseen(joiner, 404, "not-found");
    await expectRefusedUnseen(own, 401, "unauthorized");
    await expectNothingLive(2);
  });

  it("gets no credential once its ticket expires, answered as a request made now would be", async () => {
    const host = await identity(`expiring-in-flight-${scenario.day}`);
    await register(host, scenario.ip);
    // The service's own ticket for this session, issued again to expire in two seconds, as one issued almost six hours
    // ago would: the session stays live, and only the ticket runs out.
    const keys = (await serviceKeys(env.SERVICE_KEY))!;
    const issued = (await readJoinTicket(keys.ticket, await ticketFor(host.serverId, null, true), seconds()))!;
    const ticket = await issueJoinTicket(keys.ticket, { ...issued, expiresAt: seconds() + 2 });
    const release = holdProvider();
    const held = mint(host.serverId, ticket);
    await scenario.relay.mintsArrived(1);
    scenario.clock.advance(5);
    expect((await mint(host.serverId, ticket)).status).toBe(401);
    release();
    await expectRefusedUnseen(held, 401, "unauthorized");
    await expectNothingLive(1);
  });

  it("gets no credential when its host session cannot be asked again", async () => {
    const host = await identity(`unreachable-in-flight-${scenario.day}`);
    await register(host, scenario.ip);
    const ticket = await ticketFor(host.serverId, null, true);
    const release = holdProvider();
    const held = mint(host.serverId, ticket);
    await scenario.relay.mintsArrived(1);
    // The first check has already answered; the one after the provider answers fails.
    const relayGrant = HostSession.prototype.relayGrant;
    HostSession.prototype.relayGrant = () => Promise.reject(new Error("host session unreachable"));
    try {
      release();
      await expectRefusedUnseen(held, 500, "internal");
    } finally {
      HostSession.prototype.relayGrant = relayGrant;
    }
    await expectNothingLive(1);
  });
});
