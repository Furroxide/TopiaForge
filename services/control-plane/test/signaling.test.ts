import { runDurableObjectAlarm } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { JsonObject } from "../src/shared/canonical-json";
import { call, identity, register } from "./support/api";
import { endScenario, startScenario, type Scenario } from "./support/scenario";
import { connect, openSignal, type SignalClient } from "./support/sockets";

let scenario: Scenario;
const open: SignalClient[] = [];
beforeEach(() => {
  scenario = startScenario("signaling");
});
afterEach(() => {
  for (const client of open.splice(0)) client.close();
  endScenario();
});

const PEER_IP = "198.51.100.77";
const ICE = { ufrag: "abcd1234", pwd: "0123456789abcdefABCDEF" };
const HOST_CANDIDATE = { kind: "host", address: "192.168.1.20", port: 27250, priority: 2130706431 };
const SRFLX_CANDIDATE = { kind: "srflx", address: "198.51.100.77", port: 40000, priority: 1694498815 };
// Relay addresses come from the relay provider's ranges (RELAY_ADDRESS_RANGES in wrangler.toml).
const RELAY_CANDIDATE = { kind: "relay", address: "141.101.90.10", port: 50000, priority: 16777215 };

interface Room {
  readonly serverId: string;
  readonly hostToken: string;
  readonly host: SignalClient;
  readonly peer: SignalClient;
  readonly peerId: string;
  readonly ticket: string;
}

async function ticketFor(serverId: string, relayOnly: boolean, ip = PEER_IP): Promise<string> {
  const response = await call("POST", "/v1/join-tickets", { ip, body: { serverId, invite: null, relayOnly } });
  expect(response.status).toBe(201);
  return response.json["ticket"] as string;
}

async function track(client: Promise<SignalClient>): Promise<SignalClient> {
  const connected = await client;
  open.push(connected);
  return connected;
}

async function room(options: { readonly hostRelayOnly?: boolean; readonly peerRelayOnly?: boolean; readonly peerIp?: string } = {}): Promise<Room> {
  const identityOfHost = await identity(`room-${scenario.day}-${open.length}`);
  const { token } = await register(identityOfHost, scenario.ip, { relayOnly: options.hostRelayOnly ?? false });
  const host = await track(connect(identityOfHost.serverId, token, scenario.ip));
  expect(await host.next()).toEqual({ type: "welcome", role: "host", peers: [] });
  const peerIp = options.peerIp ?? PEER_IP;
  const ticket = await ticketFor(identityOfHost.serverId, options.peerRelayOnly ?? false, peerIp);
  const peer = await track(connect(identityOfHost.serverId, ticket, peerIp));
  const welcome = await peer.next();
  expect(welcome).toMatchObject({ type: "welcome", role: "peer", hostOnline: true, hostRelayOnly: options.hostRelayOnly ?? false });
  const peerId = welcome["peer"] as string;
  expect(peerId).toMatch(/^[0-9a-f]{16}$/);
  expect(await host.next()).toEqual({ type: "joined", peer: peerId, relayOnly: options.peerRelayOnly ?? false });
  return { serverId: identityOfHost.serverId, hostToken: token, host, peer, peerId, ticket };
}

describe("signaling between a host and a joining peer", () => {
  it("routes offers, answers, candidates, punches and goodbyes, naming the peer only to the host", async () => {
    const { host, peer, peerId } = await room();
    peer.send({ type: "offer", ice: ICE, candidates: [HOST_CANDIDATE, SRFLX_CANDIDATE, RELAY_CANDIDATE] });
    expect(await host.next()).toEqual({ type: "offer", peer: peerId, ice: ICE, candidates: [HOST_CANDIDATE, SRFLX_CANDIDATE, RELAY_CANDIDATE] });
    host.send({ type: "answer", peer: peerId, ice: ICE, candidates: [RELAY_CANDIDATE] });
    expect(await peer.next()).toEqual({ type: "answer", ice: ICE, candidates: [RELAY_CANDIDATE] });
    host.send({ type: "candidate", peer: peerId, candidate: SRFLX_CANDIDATE });
    expect(await peer.next()).toEqual({ type: "candidate", candidate: SRFLX_CANDIDATE });
    peer.send({ type: "punch", delayMs: 250 });
    expect(await host.next()).toEqual({ type: "punch", peer: peerId, delayMs: 250 });
    peer.send({ type: "bye", reason: "done" });
    expect(await host.next()).toEqual({ type: "bye", peer: peerId, reason: "done" });
    expect((await peer.closed()).code).toBe(1000);
    expect(await host.next()).toEqual({ type: "left", peer: peerId });
  });

  it("strips every non-relay candidate from a relay-only peer before the host sees it", async () => {
    const { host, peer, peerId } = await room({ peerRelayOnly: true });
    peer.send({ type: "offer", ice: ICE, candidates: [HOST_CANDIDATE, SRFLX_CANDIDATE, RELAY_CANDIDATE] });
    expect(await peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 2 });
    expect(await host.next()).toEqual({ type: "offer", peer: peerId, ice: ICE, candidates: [RELAY_CANDIDATE] });
    peer.send({ type: "candidate", candidate: SRFLX_CANDIDATE });
    expect(await peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 1 });
    peer.send({ type: "punch", delayMs: 0 });
    // The withheld candidate never arrives: the next frame the host sees is the punch sent after it.
    expect(await host.next()).toEqual({ type: "punch", peer: peerId, delayMs: 0 });
  });

  it("never forwards a relay-only side's local or own address, even when labelled as a relay", async () => {
    const { host, peer, peerId } = await room({ peerRelayOnly: true });
    const mislabelled = [
      { kind: "relay", address: "192.168.1.20", port: 27250, priority: 1 },
      { kind: "relay", address: "fe80::1", port: 27250, priority: 1 },
      { kind: "relay", address: PEER_IP, port: 40000, priority: 1 },
    ];
    peer.send({ type: "offer", ice: ICE, candidates: [...mislabelled, RELAY_CANDIDATE] });
    expect(await peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 3 });
    expect(await host.next()).toEqual({ type: "offer", peer: peerId, ice: ICE, candidates: [RELAY_CANDIDATE] });
  });

  it("forwards from a relay-only side only addresses inside the relay provider's ranges", async () => {
    // A peer connected over IPv6 labels an unrelated address and a neighbour in its own /64 as relays.
    const { host, peer, peerId } = await room({ peerRelayOnly: true, peerIp: "2001:db8:1:2::10" });
    const outside = [
      { kind: "relay", address: "203.0.113.77", port: 3478, priority: 1 },
      { kind: "relay", address: "2001:db8:1:2::abcd", port: 3478, priority: 1 },
    ];
    peer.send({ type: "offer", ice: ICE, candidates: [...outside, RELAY_CANDIDATE] });
    expect(await peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 2 });
    expect(await host.next()).toEqual({ type: "offer", peer: peerId, ice: ICE, candidates: [RELAY_CANDIDATE] });
    peer.send({ type: "candidate", candidate: outside[0]! });
    expect(await peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 1 });
  });

  it("withholds a relay-only side's own IPv4 address and IPv6 /64, even inside the relay ranges", async () => {
    // A side that reaches the service through the relay provider's own network has an observed address in its ranges.
    const v4 = await room({ peerRelayOnly: true, peerIp: "162.158.10.20" });
    const sameAddress = { kind: "relay", address: "162.158.10.20", port: 3478, priority: 2 };
    const neighbour = { kind: "relay", address: "162.158.10.21", port: 3478, priority: 1 };
    v4.peer.send({ type: "offer", ice: ICE, candidates: [sameAddress, neighbour] });
    expect(await v4.peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 1 });
    expect(await v4.host.next()).toEqual({ type: "offer", peer: v4.peerId, ice: ICE, candidates: [neighbour] });

    const v6 = await room({ peerRelayOnly: true, peerIp: "2a06:98c1:3200::10" });
    const sameNetwork = { kind: "relay", address: "2a06:98c1:3200::abcd", port: 3478, priority: 2 };
    const otherNetwork = { kind: "relay", address: "2a06:98c1:3201::1", port: 3478, priority: 1 };
    v6.peer.send({ type: "offer", ice: ICE, candidates: [sameNetwork, otherNetwork] });
    expect(await v6.peer.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 1 });
    expect(await v6.host.next()).toEqual({ type: "offer", peer: v6.peerId, ice: ICE, candidates: [otherNetwork] });
  });

  it("strips a relay-only host's direct candidates toward its peers", async () => {
    const { host, peer, peerId } = await room({ hostRelayOnly: true });
    host.send({ type: "answer", peer: peerId, ice: ICE, candidates: [HOST_CANDIDATE, SRFLX_CANDIDATE, RELAY_CANDIDATE] });
    expect(await host.next()).toEqual({ type: "notice", code: "candidates-withheld", withheld: 2 });
    expect(await peer.next()).toEqual({ type: "answer", ice: ICE, candidates: [RELAY_CANDIDATE] });
    // The direct peer's own candidates still reach the relay-only host: privacy protects the side that chose it.
    peer.send({ type: "candidate", candidate: SRFLX_CANDIDATE });
    expect(await host.next()).toEqual({ type: "candidate", peer: peerId, candidate: SRFLX_CANDIDATE });
  });
});

describe("signaling frames", () => {
  it("refuses malformed frames and closes a socket that keeps sending them", async () => {
    const { peer } = await room();
    const invalid: (JsonObject | string)[] = [
      { type: "answer", ice: ICE, candidates: [] },
      '{"type": "punch","delayMs":1}',
      { type: "candidate", candidate: { kind: "host", address: "2001:DB8::1", port: 1, priority: 1 } },
      { type: "offer", ice: ICE, candidates: Array.from({ length: 17 }, () => RELAY_CANDIDATE) },
      { type: "bye", reason: "because I said so" },
    ];
    for (const [index, frame] of invalid.entries()) {
      peer.send(frame);
      expect(await peer.next()).toEqual({ type: "error", code: "invalid-message" });
      if (index < invalid.length - 1) continue;
      expect((await peer.closed()).code).toBe(4400);
    }
  });

  it("closes on a binary frame", async () => {
    const { peer } = await room();
    peer.send(new Uint8Array([1, 2, 3]).buffer);
    expect((await peer.closed()).code).toBe(1003);
  });

  it("closes a socket past the frame rate limit", async () => {
    const { host, peer, peerId } = await room();
    for (let index = 0; index < 41; index++) peer.send({ type: "punch", delayMs: index });
    for (let index = 0; index < 40; index++) expect(await host.next()).toEqual({ type: "punch", peer: peerId, delayMs: index });
    expect(await peer.next()).toEqual({ type: "error", code: "rate-limited" });
    expect((await peer.closed()).code).toBe(4408);
  });

  it("tells a side when its counterpart is missing", async () => {
    const host = await identity(`absent-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const peer = await track(connect(host.serverId, await ticketFor(host.serverId, false), PEER_IP));
    expect(await peer.next()).toMatchObject({ type: "welcome", hostOnline: false });
    peer.send({ type: "punch", delayMs: 1 });
    expect(await peer.next()).toEqual({ type: "error", code: "host-offline" });
    const hostClient = await track(connect(host.serverId, token, scenario.ip));
    expect((await hostClient.next())["peers"]).toHaveLength(1);
    expect(await peer.next()).toEqual({ type: "host-status", online: true, relayOnly: false });
    hostClient.send({ type: "punch", peer: "0".repeat(16), delayMs: 1 });
    expect(await hostClient.next()).toEqual({ type: "error", code: "unknown-peer" });
  });
});

describe("signaling access", () => {
  it("authorizes every upgrade", async () => {
    const host = await identity(`guarded-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const other = await identity(`guarded-other-${scenario.day}`);
    await register(other, scenario.ip);
    const foreignTicket = await ticketFor(other.serverId, false);
    expect((await openSignal(host.serverId, null, PEER_IP)).status).toBe(401);
    expect((await openSignal(host.serverId, foreignTicket, PEER_IP)).status).toBe(401);
    expect((await openSignal(host.serverId, `tfh1_${"A".repeat(43)}`, PEER_IP)).status).toBe(401);
    expect((await call("GET", `/v1/signal/${host.serverId}`, { ip: PEER_IP, token })).status).toBe(400);
    const ticket = await ticketFor(host.serverId, false);
    scenario.clock.advance(6 * 3600);
    expect((await openSignal(host.serverId, ticket, PEER_IP)).status).toBe(401);
  });

  it("refuses an upgrade for a session that has ended", async () => {
    const host = await identity(`ended-room-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const ticket = await ticketFor(host.serverId, false);
    await call("DELETE", `/v1/hosts/${host.serverId}`, { ip: scenario.ip, token });
    const refused = await openSignal(host.serverId, ticket, PEER_IP);
    expect(refused.status).toBe(404);
    expect(refused.client === null ? refused.body : "").toBe('{"error":{"code":"not-found","message":"Not found."}}');
  });
});

describe("the signaling room", () => {
  it("replaces a reconnecting host or peer and closes the room when the session ends", async () => {
    const { serverId, hostToken, host, peer, peerId, ticket } = await room();
    const newHost = await track(connect(serverId, hostToken, scenario.ip));
    expect((await host.closed()).code).toBe(4409);
    expect(await newHost.next()).toEqual({ type: "welcome", role: "host", peers: [{ peer: peerId, relayOnly: false }] });
    expect(await peer.next()).toEqual({ type: "host-status", online: true, relayOnly: false });
    const newPeer = await track(connect(serverId, ticket, PEER_IP));
    expect((await peer.closed()).code).toBe(4409);
    expect(await newPeer.next()).toMatchObject({ type: "welcome", peer: peerId });
    expect(await newHost.next()).toEqual({ type: "joined", peer: peerId, relayOnly: false });
    await call("DELETE", `/v1/hosts/${serverId}`, { ip: scenario.ip, token: hostToken });
    expect((await newHost.closed()).code).toBe(4410);
    expect((await newPeer.closed()).code).toBe(4410);
    expect(newHost.pending()).toEqual([]);
  });

  it("closes a peer's socket when its lifetime ends", async () => {
    const { serverId, hostToken, peer } = await room();
    for (let beat = 0; beat < 31; beat++) {
      scenario.clock.advance(30);
      await call("POST", `/v1/hosts/${serverId}/heartbeat`, { ip: scenario.ip, token: hostToken, body: { players: 1 } });
    }
    await runDurableObjectAlarm(env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(serverId)));
    expect((await peer.closed()).code).toBe(4411);
  });

  it("closes an expired peer's socket on its next frame", async () => {
    const { serverId, hostToken, peer } = await room();
    // The session stays live; the last heartbeat comes just before the socket's fifteen minutes are up.
    for (let beat = 0; beat < 29; beat++) {
      scenario.clock.advance(30);
      await call("POST", `/v1/hosts/${serverId}/heartbeat`, { ip: scenario.ip, token: hostToken, body: { players: 1 } });
    }
    scenario.clock.advance(30);
    peer.send({ type: "punch", delayMs: 1 });
    expect((await peer.closed()).code).toBe(4411);
  });

  it("closes a socket whose session lapsed on its next frame, before the alarm sweeps the room", async () => {
    const { peer } = await room();
    scenario.clock.advance(91);
    peer.send({ type: "punch", delayMs: 1 });
    expect((await peer.closed()).code).toBe(4410);
  });
});
