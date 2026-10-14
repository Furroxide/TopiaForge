/**
 * The signaling room's places and its host sockets: how many sockets a room takes and from where, how a host's
 * goodbye keeps a ticket out whether or not its socket is still open, what the room remembers to do that and for how
 * long, and how a registration replaces the sockets opened under the one before it.
 */
import { runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { Registration } from "../src/objects/host-session";
import { MAXIMUM_ADMITTED_PEERS, MAXIMUM_EXCLUDED_PEERS, type HostStore } from "../src/objects/host-store";
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

/** Moves time past the fifteen minutes a peer socket lives, keeping the session live with heartbeats. */
async function outlastPeerSockets(serverId: string, token: string): Promise<void> {
  for (let beat = 0; beat < 32; beat++) {
    scenario.clock.advance(30);
    expect((await call("POST", `/v1/hosts/${serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 1 } })).status).toBe(200);
  }
}

const seconds = (): number => Math.floor(scenario.clock.now() / 1000);
const sessionOf = (serverId: string) => env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(serverId));
/** A peer id in the room's format, numbered so that no ticket carries it. */
const peerId = (index: number): string => index.toString(16).padStart(16, "0");
const storeOf = (instance: unknown): HostStore => (instance as { readonly store: HostStore }).store;

/** The peer ids a host session remembers, each with when its entry ends: those its room admitted, and those kept out. */
async function rememberedPeers(serverId: string): Promise<{ readonly admitted: Record<string, number>; readonly excluded: Record<string, number> }> {
  return runInDurableObject(sessionOf(serverId), (_, state) => {
    const read = (table: string): Record<string, number> =>
      Object.fromEntries(state.storage.sql.exec<{ peer: string; until: number }>(`SELECT peer, until FROM ${table}`).toArray().map((row) => [row.peer, row.until]));
    return { admitted: read("admitted_peer"), excluded: read("excluded_peer") };
  });
}

/**
 * Sends a host's frames no faster than the room allows, forty in any ten seconds, keeping the session live with a
 * heartbeat before each window. Each window ends with a punch for a peer that is not there: its `unknown-peer` answer
 * shows that every frame before it was handled.
 */
async function sendPaced(client: SignalClient, serverId: string, token: string, frames: readonly JsonObject[]): Promise<void> {
  for (let start = 0; start < frames.length; start += 39) {
    scenario.clock.advance(10);
    expect((await call("POST", `/v1/hosts/${serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 1 } })).status).toBe(200);
    for (const frame of frames.slice(start, start + 39)) client.send(frame);
    client.send({ type: "punch", peer: "f".repeat(16), delayMs: 0 });
    expect(await client.next()).toEqual({ type: "error", code: "unknown-peer" });
  }
}

const farewells = (peers: readonly string[]): JsonObject[] => peers.map((peer) => ({ type: "bye", peer, reason: "cancelled" }));

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
    // It stays out while its ticket could still open the room, long past the fifteen minutes a peer socket lives.
    await outlastPeerSockets(host.serverId, token);
    expect((await openSignal(host.serverId, ticket, PEER_IP)).status).toBe(401);
    // Only that ticket is kept out: the same player with a new ticket comes back under a new peer id.
    const fresh = await join(host.serverId, PEER_IP);
    expect(fresh.status).toBe(101);
    expect((await fresh.client!.next())["peer"]).not.toBe(peerId);
  });

  it("keep out a peer the host says goodbye to after its socket closed, however it closed", async () => {
    const host = await identity(`late-farewell-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    await hostClient.next();
    // One joiner closes its own socket; the service closes the other's for sending frames too fast.
    const leavingTicket = await ticketFor(host.serverId, PEER_IP);
    const leaving = await track(connect(host.serverId, leavingTicket, PEER_IP));
    const leavingId = (await leaving.next())["peer"] as string;
    await nextOf(hostClient, "joined");
    leaving.close();
    expect(await nextOf(hostClient, "left")).toEqual({ type: "left", peer: leavingId });
    const floodingTicket = await ticketFor(host.serverId, PEER_IP);
    const flooding = await track(connect(host.serverId, floodingTicket, PEER_IP));
    const floodingId = (await flooding.next())["peer"] as string;
    await nextOf(hostClient, "joined");
    for (let index = 0; index < 41; index++) flooding.send({ type: "punch", delayMs: index });
    expect((await flooding.closed()).code).toBe(4408);
    expect(await nextOf(hostClient, "left", ["punch"])).toEqual({ type: "left", peer: floodingId });
    // Neither goodbye has a socket to reach, and neither is an error: the host's next frame answers the frame after them.
    hostClient.send({ type: "bye", peer: leavingId, reason: "cancelled" });
    hostClient.send({ type: "bye", peer: floodingId, reason: "failed" });
    hostClient.send("not a frame");
    expect(await hostClient.next()).toEqual({ type: "error", code: "invalid-message" });
    for (const ticket of [leavingTicket, floodingTicket]) expect((await openSignal(host.serverId, ticket, PEER_IP)).status).toBe(401);
    await outlastPeerSockets(host.serverId, token);
    for (const ticket of [leavingTicket, floodingTicket]) expect((await openSignal(host.serverId, ticket, PEER_IP)).status).toBe(401);
  });
});

describe("a host's goodbyes", () => {
  it("are kept only for peers the room admitted, so made-up peer ids cannot push real ones out", async () => {
    const host = await identity(`made-up-farewells-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    await hostClient.next();
    // Two joiners the host says goodbye to while they are connected. The first one's ticket expires first.
    const kicked: { readonly ticket: string; readonly ip: string }[] = [];
    for (const ip of ["198.51.100.40", "192.0.2.40"]) {
      const ticket = await ticketFor(host.serverId, ip);
      const peer = await track(connect(host.serverId, ticket, ip));
      const id = (await peer.next())["peer"] as string;
      await nextOf(hostClient, "joined");
      hostClient.send({ type: "bye", peer: id, reason: "cancelled" });
      expect((await peer.closed()).code).toBe(1000);
      expect(await nextOf(hostClient, "left")).toEqual({ type: "left", peer: id });
      kicked.push({ ticket, ip });
      scenario.clock.advance(1);
    }
    // Goodbyes for ids the room never admitted, as many as would fill the list beside the two real ones, then one of
    // them again, as a host retrying would send it.
    const madeUp = Array.from({ length: MAXIMUM_EXCLUDED_PEERS - 2 }, (_, index) => peerId(index));
    await sendPaced(hostClient, host.serverId, token, farewells([...madeUp, madeUp[0]!]));
    expect((await openSignal(host.serverId, kicked[0]!.ticket, kicked[0]!.ip)).status).toBe(401);
    // Two more made-up ids leave both real goodbyes in place.
    await sendPaced(hostClient, host.serverId, token, farewells([peerId(MAXIMUM_EXCLUDED_PEERS - 2), peerId(MAXIMUM_EXCLUDED_PEERS - 1)]));
    for (const { ticket, ip } of kicked) expect((await openSignal(host.serverId, ticket, ip)).status).toBe(401);
    expect(Object.keys((await rememberedPeers(host.serverId)).excluded)).toHaveLength(2);
  });

  it("make room on a full list only for a new peer, pushing out the one whose ticket expires first", async () => {
    const host = await identity(`farewell-cap-${scenario.day}`);
    await register(host, scenario.ip);
    const now = seconds();
    const outcome = await runInDurableObject(sessionOf(host.serverId), (instance) => {
      const store = storeOf(instance);
      // Admitted peers whose tickets expire a second apart, the first soonest, and goodbyes to all but the last.
      for (let index = 0; index <= MAXIMUM_EXCLUDED_PEERS; index++) store.admitPeer(peerId(index), now + 3600 + index);
      for (let index = 0; index < MAXIMUM_EXCLUDED_PEERS; index++) store.excludePeer(peerId(index), now, null);
      // A host repeating every goodbye on the full list pushes none of them out.
      for (let index = 0; index < MAXIMUM_EXCLUDED_PEERS; index++) store.excludePeer(peerId(index), now, null);
      const afterRepeats = Array.from({ length: MAXIMUM_EXCLUDED_PEERS }, (_, index) => store.isExcluded(peerId(index), now));
      // A goodbye to one more peer pushes out the goodbye whose ticket expires first, and only that one.
      store.excludePeer(peerId(MAXIMUM_EXCLUDED_PEERS), now, null);
      const afterNew = Array.from({ length: MAXIMUM_EXCLUDED_PEERS + 1 }, (_, index) => store.isExcluded(peerId(index), now));
      return { afterRepeats, afterNew };
    });
    expect(outcome.afterRepeats.every(Boolean)).toBe(true);
    expect(outcome.afterNew).toEqual([false, ...Array<boolean>(MAXIMUM_EXCLUDED_PEERS).fill(true)]);
  });

  it("keep a peer whose socket is gone out until its ticket expires, and are remembered no longer", async () => {
    const host = await identity(`farewell-expiry-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    await hostClient.next();
    const issued = await call("POST", "/v1/join-tickets", { ip: PEER_IP, body: { serverId: host.serverId, invite: null, relayOnly: false } });
    const ticket = issued.json["ticket"] as string;
    const expiresAt = issued.json["expiresAt"] as number;
    const peer = await track(connect(host.serverId, ticket, PEER_IP));
    const id = (await peer.next())["peer"] as string;
    await nextOf(hostClient, "joined");
    peer.close();
    expect(await nextOf(hostClient, "left")).toEqual({ type: "left", peer: id });
    // A minute later the host says goodbye to it, and to an id the room never admitted, which changes nothing and is
    // not answered: the host's next frame answers the one after them.
    for (let beat = 0; beat < 2; beat++) {
      scenario.clock.advance(30);
      expect((await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, { ip: scenario.ip, token, body: { players: 1 } })).status).toBe(200);
    }
    hostClient.send({ type: "bye", peer: id, reason: "done" });
    hostClient.send({ type: "bye", peer: "f".repeat(16), reason: "done" });
    hostClient.send("not a frame");
    expect(await hostClient.next()).toEqual({ type: "error", code: "invalid-message" });
    expect((await openSignal(host.serverId, ticket, PEER_IP)).status).toBe(401);
    // Both the admission and the goodbye end exactly when the ticket does, not six hours after the goodbye.
    expect(await rememberedPeers(host.serverId)).toEqual({ admitted: { [id]: expiresAt }, excluded: { [id]: expiresAt } });
    await runInDurableObject(sessionOf(host.serverId), (instance) => storeOf(instance).purgeExpired(expiresAt));
    expect(await rememberedPeers(host.serverId)).toEqual({ admitted: {}, excluded: {} });
  });

  it("are forgotten, with every admission, once the session ends", async () => {
    const host = await identity(`farewell-end-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    await hostClient.next();
    const peer = await track(connect(host.serverId, await ticketFor(host.serverId, PEER_IP), PEER_IP));
    const id = (await peer.next())["peer"] as string;
    await nextOf(hostClient, "joined");
    hostClient.send({ type: "bye", peer: id, reason: "done" });
    expect((await peer.closed()).code).toBe(1000);
    const remembered = await rememberedPeers(host.serverId);
    expect([Object.keys(remembered.admitted), Object.keys(remembered.excluded)]).toEqual([[id], [id]]);
    expect((await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token })).status).toBe(204);
    // The registration's spent challenge outlives the session by up to two minutes; the next alarm then wipes it all.
    scenario.clock.advance(121);
    expect(await runDurableObjectAlarm(sessionOf(host.serverId))).toBe(true);
    const tables = await runInDurableObject(sessionOf(host.serverId), (_, state) =>
      state.storage.sql.exec<{ name: string }>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE '\\_cf\\_%' ESCAPE '\\' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\'").toArray(),
    );
    expect(tables).toEqual([]);
  });

  it("rest on a capped list of admissions, yet still keep out a connected peer whose admission made way", async () => {
    const host = await identity(`admission-cap-${scenario.day}`);
    await register(host, scenario.ip);
    const now = seconds();
    const outcome = await runInDurableObject(sessionOf(host.serverId), (instance, state) => {
      const store = storeOf(instance);
      const admitted = (): string[] => state.storage.sql.exec<{ peer: string }>("SELECT peer FROM admitted_peer ORDER BY until").toArray().map((row) => row.peer);
      for (let index = 0; index < MAXIMUM_ADMITTED_PEERS; index++) store.admitPeer(peerId(index), now + 3600 + index);
      // The same ticket admitted again, as a reconnect does, pushes nothing out.
      store.admitPeer(peerId(0), now + 3600);
      const afterRepeat = admitted();
      store.admitPeer(peerId(MAXIMUM_ADMITTED_PEERS), now + 3600 + MAXIMUM_ADMITTED_PEERS);
      const afterNew = admitted();
      return {
        afterRepeat: [afterRepeat.length, afterRepeat[0]],
        afterNew: [afterNew.length, afterNew[0], afterNew.at(-1)],
        // Without its admission, a goodbye keeps the peer out only while the room still lists a socket for it.
        gone: store.excludePeer(peerId(0), now, null),
        connected: store.excludePeer(peerId(0), now, now + 3600),
      };
    });
    expect(outcome).toEqual({
      afterRepeat: [MAXIMUM_ADMITTED_PEERS, peerId(0)],
      afterNew: [MAXIMUM_ADMITTED_PEERS, peerId(1), peerId(MAXIMUM_ADMITTED_PEERS)],
      gone: false,
      connected: true,
    });
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
