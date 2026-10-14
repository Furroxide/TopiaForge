/**
 * The signaling room's places and its host sockets: how many sockets a room takes and from where, how a host's
 * goodbye keeps a ticket out, and how a registration replaces the sockets opened under the one before it.
 */
import { runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { Registration } from "../src/objects/host-session";
import type { JsonObject } from "../src/shared/canonical-json";
import { verifySignedDescriptor } from "../src/shared/descriptor";
import { summarize } from "../src/shared/listing";
import { hostTokenHash, newHostToken } from "../src/shared/tokens";
import { utf8 } from "../src/shared/wire-text";
import { call, identity, register, signedDescriptor, type HostIdentity } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";
import { connect, openSignal, type SignalClient, type Upgrade } from "./support/sockets";

let scenario: Scenario;
const open: SignalClient[] = [];
beforeEach(() => {
  scenario = startScenario("signaling room");
});
afterEach(() => {
  for (const client of open.splice(0)) client.close();
  endScenario();
});

const PEER_IP = "198.51.100.77";
const ICE = { ufrag: "abcd1234", pwd: "0123456789abcdefABCDEF" };
const SRFLX_CANDIDATE = { kind: "srflx", address: "203.0.113.9", port: 27250, priority: 1694498815 };
const RELAY_CANDIDATE = { kind: "relay", address: "141.101.90.11", port: 50001, priority: 16777215 };
const ROOM_FULL = '{"error":{"code":"room-full","message":"The signaling room is full."}}';

async function track(client: Promise<SignalClient>): Promise<SignalClient> {
  const connected = await client;
  open.push(connected);
  return connected;
}

async function ticketFor(serverId: string, ip: string): Promise<string> {
  const response = await call("POST", "/v1/join-tickets", { ip, body: { serverId, invite: null, relayOnly: false } });
  expect(response.status).toBe(201);
  return response.json["ticket"] as string;
}

/** A joiner's upgrade from `ip` with a fresh ticket; its socket is closed after the test. */
async function join(serverId: string, ip: string): Promise<Upgrade> {
  const upgrade = await openSignal(serverId, await ticketFor(serverId, ip), ip);
  if (upgrade.client !== null) open.push(upgrade.client);
  return upgrade;
}

/**
 * The next frame of `type`, skipping only frames of the `skipping` types: host-status updates, whose order depends on
 * when old sockets finish closing, unless told otherwise.
 */
async function nextOf(client: SignalClient, type: string, skipping: readonly string[] = ["host-status"]): Promise<JsonObject> {
  for (;;) {
    const frame = await client.next();
    if (frame["type"] === type) return frame;
    expect(skipping).toContain(frame["type"]);
  }
}

describe("a signaling room's places", () => {
  it("never let one /24 or /56 take more than its share", async () => {
    const host = await identity(`share-${scenario.day}`);
    await register(host, scenario.ip, { maxPlayers: 8 });
    // Eight players make ten places: seven joiners and three spare. One network may hold four of them.
    for (let index = 0; index < 4; index++) expect((await join(host.serverId, `198.51.100.${10 + index}`)).status).toBe(101);
    const fifth = await join(host.serverId, "198.51.100.14");
    expect(fifth.status).toBe(409);
    expect(fifth.client === null ? fifth.body : "").toBe(ROOM_FULL);
    for (let index = 0; index < 4; index++) expect((await join(host.serverId, `2001:db8:aa:bb${index}0::1`)).status).toBe(101);
    expect((await join(host.serverId, "2001:db8:aa:bbff::1")).status).toBe(409);
    expect((await join(host.serverId, "192.0.2.10")).status).toBe(101);
    expect((await join(host.serverId, "192.0.2.11")).status).toBe(101);
    expect((await join(host.serverId, "198.18.0.1")).status).toBe(409);
  });

  it("are sized to the session's players, and free up when a joiner leaves", async () => {
    const host = await identity(`sized-${scenario.day}`);
    const { token } = await register(host, scenario.ip, { maxPlayers: 2 });
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    await hostClient.next();
    // Two players make four places, at most two from one network.
    const first = await join(host.serverId, "198.51.100.20");
    expect(first.status).toBe(101);
    expect((await join(host.serverId, "198.51.100.21")).status).toBe(101);
    expect((await join(host.serverId, "198.51.100.22")).status).toBe(409);
    expect((await join(host.serverId, "192.0.2.20")).status).toBe(101);
    expect((await join(host.serverId, "192.0.2.21")).status).toBe(101);
    expect((await join(host.serverId, "198.18.0.1")).status).toBe(409);
    const leaving = first.client!;
    leaving.send({ type: "bye", reason: "done" });
    expect(await nextOf(hostClient, "bye", ["joined"])).toMatchObject({ type: "bye", reason: "done" });
    expect((await join(host.serverId, "198.18.0.1")).status).toBe(101);
  });

  it("count only open sockets, so a place the service has just closed is free at once", async () => {
    const host = await identity(`closing-${scenario.day}`);
    await register(host, scenario.ip, { maxPlayers: 2 });
    for (const ip of ["198.51.100.30", "198.51.100.31", "192.0.2.30", "192.0.2.31"]) expect((await join(host.serverId, ip)).status).toBe(101);
    const session = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    const status = await runInDurableObject(session, async (instance, state) => {
      // Until a closed socket's client finishes the close, the runtime still lists the socket. An upgrade in that
      // window, here in the same turn, must not find the room full.
      state.getWebSockets("peer")[0]!.close(1000, "bye");
      const store = (instance as unknown as { store: { session(): { epoch: string } | null } }).store;
      const headers = {
        upgrade: "websocket",
        "x-signal-server": host.serverId,
        "x-signal-role": "peer",
        "x-signal-peer": "0123456789abcdef",
        "x-signal-relay-only": "0",
        "x-signal-ticket-expires": String(Math.floor(scenario.clock.now() / 1000) + 3600),
        "x-signal-epoch": store.session()!.epoch,
        "x-signal-source": "f".repeat(32),
      };
      const response = await instance.fetch(new Request("https://host-session.internal/signal", { headers }));
      response.webSocket?.accept();
      response.webSocket?.close();
      return response.status;
    });
    expect(status).toBe(101);
  });

  it("keep a peer the host said goodbye to out for the rest of the session", async () => {
    const host = await identity(`farewell-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    await hostClient.next();
    const ticket = await ticketFor(host.serverId, PEER_IP);
    const peer = await track(connect(host.serverId, ticket, PEER_IP));
    const peerId = (await peer.next())["peer"] as string;
    await nextOf(hostClient, "joined");
    hostClient.send({ type: "bye", peer: peerId, reason: "cancelled" });
    expect(await peer.next()).toEqual({ type: "bye", reason: "cancelled" });
    expect((await peer.closed()).code).toBe(1000);
    const again = await openSignal(host.serverId, ticket, PEER_IP);
    expect(again.status).toBe(401);
    // Only that ticket is kept out: the same player with a new ticket comes back under a new peer id.
    const fresh = await join(host.serverId, PEER_IP);
    expect(fresh.status).toBe(101);
    expect((await fresh.client!.next())["peer"]).not.toBe(peerId);
  });
});

describe("host sockets across registrations", () => {
  it("close when the host registers again, and the new socket keeps to the new privacy choice", async () => {
    const host = await identity(`reregister-${scenario.day}`);
    const first = await register(host, scenario.ip, { relayOnly: false });
    const oldHost = await track(connect(host.serverId, first.token, scenario.ip));
    await oldHost.next();
    const peer = await track(connect(host.serverId, await ticketFor(host.serverId, PEER_IP), PEER_IP));
    const peerId = (await peer.next())["peer"] as string;
    expect(await oldHost.next()).toEqual({ type: "joined", peer: peerId, relayOnly: false });
    const second = await register(host, scenario.ip, { relayOnly: true });
    expect((await oldHost.closed()).code).toBe(4409);
    expect((await openSignal(host.serverId, first.token, scenario.ip)).status).toBe(401);
    const newHost = await track(connect(host.serverId, second.token, scenario.ip));
    expect(await newHost.next()).toEqual({ type: "welcome", role: "host", peers: [{ peer: peerId, relayOnly: false }] });
    newHost.send({ type: "answer", peer: peerId, ice: ICE, candidates: [SRFLX_CANDIDATE, RELAY_CANDIDATE] });
    expect(await newHost.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 1 });
    expect(await nextOf(peer, "answer")).toEqual({ type: "answer", ice: ICE, candidates: [RELAY_CANDIDATE] });
  });

  it("accept no socket under the token a registration is replacing, even while it is mid-way", async () => {
    const host = await identity(`mid-registration-${scenario.day}`);
    const first = await register(host, scenario.ip, { relayOnly: false });
    const oldTokenHash = (await hostTokenHash(first.token))!;
    const input = await registrationInput(host, true);
    const session = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    const outcome = await runInDurableObject(session, async (instance) => {
      const helpers = instance as unknown as Record<string, unknown>;
      let entered = (): void => undefined;
      const inWindow = new Promise<void>((resolve) => (entered = resolve));
      let release = (): void => undefined;
      const held = new Promise<void>((resolve) => (release = resolve));
      // Holding the registration's listing call keeps it between its start and the moment it saves the new token.
      helpers["directory"] = () => ({
        remove: async () => undefined,
        upsert: async () => {
          entered();
          await held;
          return true;
        },
      });
      try {
        const registration = instance.register(input);
        await inWindow;
        const headers = { upgrade: "websocket", "x-signal-server": host.serverId, "x-signal-role": "host", "x-signal-token-hash": oldTokenHash };
        const upgrade = instance.fetch(new Request("https://host-session.internal/signal", { headers }));
        release();
        const [registered, response] = await Promise.all([registration, upgrade]);
        response.webSocket?.accept();
        response.webSocket?.close();
        return { registered: registered.ok, status: response.status };
      } finally {
        delete helpers["directory"];
      }
    });
    expect(outcome).toEqual({ registered: true, status: 401 });
  });
});

/** A registration as the Worker hands it to the host session, after it has checked the proof. */
async function registrationInput(host: HostIdentity, relayOnly: boolean): Promise<Registration> {
  const descriptor = await signedDescriptor(host, { issuedAt: 1893456001 });
  const verified = await verifySignedDescriptor(utf8(descriptor), host.serverId);
  if (!verified.ok) throw new Error("the test descriptor does not verify");
  return {
    serverId: host.serverId,
    descriptor,
    descriptorSha256: verified.signed.sha256,
    issuedAt: verified.signed.descriptor.issuedAt,
    visibility: "public",
    summary: summarize(verified.signed.descriptor),
    tokenHash: (await hostTokenHash(newHostToken(new Uint8Array(32).fill(7))))!,
    challengeNonce: "a mid-registration test nonce",
    challengeExpiresAt: Math.floor(scenario.clock.now() / 1000) + 120,
    relayOnly,
  };
}
