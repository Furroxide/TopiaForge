/**
 * Join tickets hold only while the admission they were issued under does: the same host session, and the invite they
 * were issued with still live, or, for a ticket issued without one, a session that is still public. Signaling
 * upgrades and relay credentials both check, and the room closes the socket of a ticket that stops holding.
 */
import { runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
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

async function ticketFor(serverId: string, invite: string | null): Promise<string> {
  const response = await call("POST", "/v1/join-tickets", { ip: PEER_IP, body: { serverId, invite, relayOnly: false } });
  expect(response.status).toBe(201);
  return response.json["ticket"] as string;
}

async function signal(serverId: string, ticket: string): Promise<Upgrade> {
  const upgrade = await openSignal(serverId, ticket, PEER_IP);
  if (upgrade.client !== null) open.push(upgrade.client);
  return upgrade;
}

const mint = (serverId: string, ticket: string): Promise<ApiResult> => call("POST", "/v1/turn/credentials", { ip: PEER_IP, token: ticket, body: { serverId } });
const inviteCode = (result: ApiResult): string => (result.json["invite"] as Record<string, string>)["code"]!;

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
