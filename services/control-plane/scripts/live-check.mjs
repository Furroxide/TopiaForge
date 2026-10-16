// End-to-end check of a deployed control plane, as a host and a joining player would use it.
//
//   node scripts/live-check.mjs https://mp-staging.topiaforge.dev ../..
//
// It uses a freshly generated, invite-only test server named "TopiaForge staging check", so nothing is listed
// publicly and no fixture data reaches the service. Tokens, tickets and relay credentials stay inside this process:
// only their shapes are printed. The player's own public address, which TURN reports back, is never printed.
import crypto from "node:crypto";
import dgram from "node:dgram";
import dns from "node:dns/promises";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const origin = process.argv[2];
const repo = process.argv[3] ? path.resolve(process.argv[3]) : undefined;
if (!origin || !repo) throw new Error("usage: node scripts/live-check.mjs <origin> <repo-root>");
// The service's own dev dependencies carry the ws client, which can send an Authorization header.
const WebSocketClient = createRequire(path.join(repo, "services/control-plane/package.json"))("ws");

const results = [];
function check(name, ok, detail = "") {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} ${name}${detail ? ` (${detail})` : ""}`);
}
const shape = (token) => (typeof token === "string" ? `${token.slice(0, 5)}… ${token.length} chars` : String(token));

// ---- canonical JSON (MultiplayerLiveSessions.md section 2; ASCII text only here) ----
function canonical(value) {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(",")}}`;
}
const b64url = (buffer) => Buffer.from(buffer).toString("base64url");
const sha256Hex = (data) => crypto.createHash("sha256").update(data).digest("hex");

async function api(method, route, { token, body } = {}) {
  const headers = {};
  if (token) headers.authorization = `Bearer ${token}`;
  let payload;
  if (body !== undefined) {
    payload = canonical(body);
    headers["content-type"] = "application/json";
  }
  const response = await fetch(`${origin}${route}`, { method, headers, body: payload, redirect: "manual" });
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  return { status: response.status, json, headers: response.headers };
}

// ---- a fresh host identity and its signed descriptor ----
function newHost() {
  const signing = crypto.generateKeyPairSync("ed25519");
  const transport = crypto.generateKeyPairSync("x25519");
  const signingRaw = Buffer.from(signing.publicKey.export({ format: "jwk" }).x, "base64url");
  const transportRaw = Buffer.from(transport.publicKey.export({ format: "jwk" }).x, "base64url");
  return { signing, signingKey: b64url(signingRaw), transportKey: b64url(transportRaw), serverId: sha256Hex(signingRaw) };
}

function signedDescriptor(host) {
  const fixtures = JSON.parse(fs.readFileSync(path.join(repo, "tests/fixtures/multiplayer/descriptor/cases.json"), "utf8"));
  const template = fixtures.valid[0].descriptor;
  const packages = [];
  const descriptor = {
    ...template,
    schema: "topiaforge.server-descriptor/1",
    serverId: host.serverId,
    signingKey: host.signingKey,
    transportKey: host.transportKey,
    issuedAt: Math.floor(Date.now() / 1000),
    name: "TopiaForge staging check",
    maxPlayers: 4,
    passwordRequired: false,
    voice: false,
    visibility: "invite",
    hostServesPackages: false,
    gamemode: "Open Sandbox",
    world: null,
    packages,
    packageDigest: sha256Hex(canonical(packages)),
  };
  const label = Buffer.from("TopiaForge server descriptor v1\n", "ascii");
  const signature = crypto.sign(null, Buffer.concat([label, Buffer.from(canonical(descriptor), "utf8")]), host.signing.privateKey);
  return canonical({ descriptor, signature: b64url(signature) });
}

function registrationProof(host, unsigned) {
  const label = Buffer.from("TopiaForge host registration v1\n", "ascii");
  return b64url(crypto.sign(null, Buffer.concat([label, Buffer.from(canonical(unsigned), "utf8")]), host.signing.privateKey));
}

// ---- signaling ----
function openSocket(route, token) {
  return new Promise((resolve, reject) => {
    const url = `${origin.replace(/^https/, "wss")}${route}`;
    const socket = new WebSocketClient(url, { headers: { authorization: `Bearer ${token}` } });
    const frames = [];
    const waiters = [];
    socket.on("message", (data) => {
      const frame = JSON.parse(data.toString());
      frames.push(frame);
      for (const waiter of [...waiters]) if (waiter.match(frame)) { waiters.splice(waiters.indexOf(waiter), 1); waiter.resolve(frame); }
    });
    socket.on("open", () => resolve({
      socket,
      frames,
      send: (frame) => socket.send(canonical(frame)),
      next: (match, ms = 8000) => new Promise((ok, fail) => {
        const found = frames.find(match);
        if (found) return ok(found);
        const waiter = { match, resolve: ok };
        waiters.push(waiter);
        setTimeout(() => { const at = waiters.indexOf(waiter); if (at >= 0) { waiters.splice(at, 1); fail(new Error("timed out waiting for a frame")); } }, ms);
      }),
    }));
    socket.on("unexpected-response", (_request, response) => reject(new Error(`WebSocket to ${route} answered ${response.statusCode}`)));
    socket.on("error", (error) => reject(new Error(`WebSocket to ${route} failed: ${error.message}`)));
  });
}

// ---- a small TURN client (RFC 8489 / 8656): enough to allocate, permit, bind a channel and relay ----
const MAGIC = 0x2112a442;
function stunMessage(type, attributes, key) {
  const transaction = crypto.randomBytes(12);
  const encode = (list) => Buffer.concat(list.map(([attrType, value]) => {
    const header = Buffer.alloc(4); header.writeUInt16BE(attrType, 0); header.writeUInt16BE(value.length, 2);
    const pad = Buffer.alloc((4 - (value.length % 4)) % 4);
    return Buffer.concat([header, value, pad]);
  }));
  let body = encode(attributes);
  const header = (length) => { const h = Buffer.alloc(20); h.writeUInt16BE(type, 0); h.writeUInt16BE(length, 2); h.writeUInt32BE(MAGIC, 4); transaction.copy(h, 8); return h; };
  if (key) {
    const forHmac = Buffer.concat([header(body.length + 24), body]);
    const integrity = crypto.createHmac("sha1", key).update(forHmac).digest();
    body = Buffer.concat([body, encode([[0x0008, integrity]])]);
  }
  return { bytes: Buffer.concat([header(body.length), body]), transaction };
}
function parseStun(buffer) {
  if (buffer.length < 20 || (buffer[0] & 0xc0) !== 0 || buffer.readUInt32BE(4) !== MAGIC) return null;
  const type = buffer.readUInt16BE(0), length = buffer.readUInt16BE(2);
  const attributes = new Map();
  for (let at = 20; at + 4 <= 20 + length;) {
    const attrType = buffer.readUInt16BE(at), size = buffer.readUInt16BE(at + 2);
    attributes.set(attrType, buffer.subarray(at + 4, at + 4 + size));
    at += 4 + size + ((4 - (size % 4)) % 4);
  }
  return { type, transaction: buffer.subarray(8, 20), attributes };
}
function xorAddress(value, transaction) {
  if (!value) return null;
  const family = value[1];
  const port = value.readUInt16BE(2) ^ (MAGIC >>> 16);
  if (family === 1) {
    const raw = value.readUInt32BE(4) ^ MAGIC;
    return { family: 4, address: [raw >>> 24, (raw >>> 16) & 255, (raw >>> 8) & 255, raw & 255].join("."), port };
  }
  const mask = Buffer.concat([Buffer.from([0x21, 0x12, 0xa4, 0x42]), transaction]);
  const bytes = Buffer.from(value.subarray(4, 20)).map((b, i) => b ^ mask[i]);
  return { family: 6, address: Buffer.from(bytes).toString("hex").match(/.{4}/g).join(":"), port };
}
function xorPeer(address, port) {
  const value = Buffer.alloc(8); value[1] = 1; value.writeUInt16BE(port ^ (MAGIC >>> 16), 2);
  const raw = address.split(".").reduce((n, part) => ((n << 8) | Number(part)) >>> 0, 0);
  value.writeUInt32BE((raw ^ MAGIC) >>> 0, 4);
  return value;
}

class TurnAllocation {
  constructor(server, username, credential) {
    this.server = server; this.username = username; this.credential = credential;
    this.socket = dgram.createSocket("udp4");
    this.pending = new Map(); this.received = [];
    this.socket.on("message", (message) => {
      if (message.length >= 4 && message[0] >= 0x40 && message[0] <= 0x4f) {
        this.received.push({ kind: "channel", channel: message.readUInt16BE(0), data: message.subarray(4, 4 + message.readUInt16BE(2)).toString() });
        return;
      }
      const parsed = parseStun(message);
      if (!parsed) return;
      if (parsed.type === 0x0017) { // Data indication
        this.received.push({ kind: "indication", data: parsed.attributes.get(0x0013)?.toString() ?? "" });
        return;
      }
      const waiter = this.pending.get(parsed.transaction.toString("hex"));
      if (waiter) { this.pending.delete(parsed.transaction.toString("hex")); waiter(parsed); }
    });
  }
  bind() { return new Promise((resolve) => this.socket.bind(0, "0.0.0.0", resolve)); }
  request(type, attributes, signed = true) {
    const withAuth = signed && this.key
      ? [...attributes, [0x0006, Buffer.from(this.username)], [0x0014, this.realm], [0x0015, this.nonce]]
      : attributes;
    const { bytes, transaction } = stunMessage(type, withAuth, signed ? this.key : undefined);
    return new Promise((resolve, reject) => {
      const id = transaction.toString("hex");
      let tries = 0;
      const send = () => { this.socket.send(bytes, this.server.port, this.server.address); };
      const timer = setInterval(() => { if (++tries > 6) { clearInterval(timer); this.pending.delete(id); reject(new Error("TURN request timed out")); } else send(); }, 500);
      this.pending.set(id, (parsed) => { clearInterval(timer); resolve(parsed); });
      send();
    });
  }
  async allocate() {
    const transport = Buffer.from([17, 0, 0, 0]);
    const first = await this.request(0x0003, [[0x0019, transport]], false);
    if (first.type !== 0x0113) throw new Error(`expected 401 to the first Allocate, got type 0x${first.type.toString(16)}`);
    this.realm = first.attributes.get(0x0014); this.nonce = first.attributes.get(0x0015);
    this.key = crypto.createHash("md5").update(`${this.username}:${this.realm.toString()}:${this.credential}`).digest();
    const answer = await this.request(0x0003, [[0x0019, transport], [0x000d, Buffer.from([0, 0, 0x02, 0x58])]]);
    if (answer.type !== 0x0103) throw new Error(`Allocate failed, type 0x${answer.type.toString(16)} code ${answer.attributes.get(0x0009)?.readUInt8(2)}${answer.attributes.get(0x0009)?.readUInt8(3)}`);
    this.relayed = xorAddress(answer.attributes.get(0x0016), answer.transaction);
    return this.relayed;
  }
  async permit(peer) {
    const answer = await this.request(0x0008, [[0x0012, xorPeer(peer.address, peer.port)]]);
    return answer.type === 0x0108;
  }
  async channel(number, peer) {
    const value = Buffer.alloc(4); value.writeUInt16BE(number, 0);
    const answer = await this.request(0x0009, [[0x000c, value], [0x0012, xorPeer(peer.address, peer.port)]]);
    return answer.type === 0x0109;
  }
  sendChannel(number, text) {
    const data = Buffer.from(text); const header = Buffer.alloc(4); header.writeUInt16BE(number, 0); header.writeUInt16BE(data.length, 2);
    const pad = Buffer.alloc((4 - (data.length % 4)) % 4);
    this.socket.send(Buffer.concat([header, data, pad]), this.server.port, this.server.address);
  }
  async release() { try { await this.request(0x0004, [[0x000d, Buffer.alloc(4)]]); } catch { /* best effort */ } this.socket.close(); }
}

function inRanges(address, ranges) {
  const value = address.split(".").reduce((n, part) => ((n << 8) | Number(part)) >>> 0, 0);
  return ranges.some((range) => {
    const [base, bits] = range.split("/");
    if (base.includes(":")) return false;
    const start = base.split(".").reduce((n, part) => ((n << 8) | Number(part)) >>> 0, 0);
    const mask = Number(bits) === 0 ? 0 : (0xffffffff << (32 - Number(bits))) >>> 0;
    return ((value & mask) >>> 0) === ((start & mask) >>> 0);
  });
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// ---- the run ----
const status = await api("GET", "/v1/status");
check("status answers", status.status === 200 && status.json?.schema === "topiaforge.control-plane-status/1", `features ${canonical(status.json?.features)}, killSwitch ${status.json?.killSwitch}`);
check("responses are not cached", (status.headers.get("cache-control") ?? "").includes("no-store"));

const host = newHost();
const challenge = await api("POST", "/v1/hosts/challenge", { body: { signingKey: host.signingKey } });
check("challenge issued", challenge.status === 200 && typeof challenge.json?.challenge === "string");
const unsigned = { challenge: challenge.json.challenge, descriptor: signedDescriptor(host), relayOnly: true };
const registered = await api("POST", "/v1/hosts", { body: { ...unsigned, proof: registrationProof(host, unsigned) } });
check("host registers", registered.status === 201 && registered.json?.serverId === host.serverId, `status ${registered.status}${registered.json?.code ? ` ${registered.json.code}` : ""}, token ${shape(registered.json?.sessionToken)}`);
if (registered.status !== 201) process.exit(1);
const token = registered.json.sessionToken;
const invite = registered.json.invite.code;
check("an invite-only server is not listed", registered.json.listed === false);

try {
  await sleep(6000); // the service wants at least 5 s between a host's heartbeats, and registering counts as one
  const beat = await api("POST", `/v1/hosts/${host.serverId}/heartbeat`, { token, body: { players: 1 } });
  check("heartbeat accepted", beat.status === 200, `status ${beat.status} ${canonical(beat.json)}`);

  const view = await api("GET", `/v1/hosts/${host.serverId}`, { token });
  check("host view", view.status === 200 && view.json?.relayOnly === true);

  const resolved = await api("POST", "/v1/invites/resolve", { body: { code: invite } });
  check("invite resolves to this server", resolved.status === 200 && resolved.json?.serverId === host.serverId);
  const miss = await api("POST", "/v1/invites/resolve", { body: { code: "ZZZZZ00000" } });
  check("an unknown invite is a plain 404", miss.status === 404);

  const described = JSON.parse(unsigned.descriptor).descriptor;
  const listing = await api("GET", `/v1/servers?build=${encodeURIComponent(described.gameBuild)}&protocol=${described.protocolVersion.split(".")[0]}`);
  check("browsing answers", listing.status === 200, `${listing.json?.servers?.length ?? "?"} public servers`);
  check("the invite-only server is not in the browser", !(listing.json?.servers ?? []).some((s) => s.serverId === host.serverId));

  const ticket = await api("POST", "/v1/join-tickets", { body: { invite, relayOnly: true, serverId: host.serverId } });
  check("join ticket issued", ticket.status === 201, `ticket ${shape(ticket.json?.ticket)}`);

  // Relay credentials for both sides, then two real allocations that relay to each other.
  const ranges = (fs.readFileSync(path.join(repo, "services/control-plane/wrangler.toml"), "utf8").match(/^RELAY_ADDRESS_RANGES = "([^"]*)"$/m)?.[1] ?? "").split(" ");
  if (status.json?.features?.relay !== true) {
    check("relay is configured", false, "features.relay is false: set the TURN secrets first");
  } else {
    const hostMint = await api("POST", "/v1/turn/credentials", { token, body: { serverId: host.serverId } });
    const peerTicket = await api("POST", "/v1/join-tickets", { body: { invite, relayOnly: true, serverId: host.serverId } });
    const joinTicket = peerTicket.json?.ticket;
    const peerMint = await api("POST", "/v1/turn/credentials", { token: peerTicket.json?.ticket, body: { serverId: host.serverId } });
    check("relay credentials minted for host and peer", hostMint.status === 201 && peerMint.status === 201, `${hostMint.status}/${peerMint.status}`);
    const urls = [...(hostMint.json?.iceServers ?? []), ...(peerMint.json?.iceServers ?? [])].flatMap((s) => s.urls);
    check("relay URLs name hosts, never addresses", urls.length > 0 && urls.every((u) => !/\d+\.\d+\.\d+\.\d+|\[/.test(u)), urls.filter((u) => u.startsWith("turn:")).join(" "));
    const entry = (mint) => mint.json.iceServers.find((s) => s.username);
    const udp = (mint) => entry(mint).urls.find((u) => /^turn:[^?]+:3478\?transport=udp$/.test(u)) ?? entry(mint).urls.find((u) => u.startsWith("turn:") && u.includes("transport=udp"));
    const [, hostName, port] = /^turn:([^:?]+):(\d+)/.exec(udp(hostMint));
    const server = { address: (await dns.lookup(hostName, { family: 4 })).address, port: Number(port) };
    const a = new TurnAllocation(server, entry(hostMint).username, entry(hostMint).credential);
    const b = new TurnAllocation(server, entry(peerMint).username, entry(peerMint).credential);
    await a.bind(); await b.bind();
    try {
      const ra = await a.allocate(); const rb = await b.allocate();
      check("relay addresses are IPv4", ra.family === 4 && rb.family === 4, `${ra.address}:${ra.port}, ${rb.address}:${rb.port}`);
      check("relay addresses lie inside RELAY_ADDRESS_RANGES", inRanges(ra.address, ranges) && inRanges(rb.address, ranges));
      check("relay ports lie in 9024-65535", ra.port >= 9024 && rb.port >= 9024);
      check("permissions granted", (await a.permit(rb)) && (await b.permit(ra)));
      check("channels bound", (await a.channel(0x4000, rb)) && (await b.channel(0x4001, ra)));
      a.sendChannel(0x4000, "smoke from A");
      b.sendChannel(0x4001, "smoke from B");
      await sleep(1500);
      check("two allocations relay to each other", b.received.some((m) => m.data.startsWith("smoke from A")) && a.received.some((m) => m.data.startsWith("smoke from B")),
        `A got ${a.received.map((m) => m.kind).join(",") || "nothing"}, B got ${b.received.map((m) => m.kind).join(",") || "nothing"}`);

      // Signaling: a relay-only peer's host candidate must be withheld; its relay candidate forwarded.
      const hostSocket = await openSocket(`/v1/signal/${host.serverId}`, token);
      await hostSocket.next((f) => f.type === "welcome");
      const peerSocket = await openSocket(`/v1/signal/${host.serverId}`, joinTicket);
      const welcome = await peerSocket.next((f) => f.type === "welcome");
      check("peer welcomed", welcome.role === "peer" && welcome.hostOnline === true);
      await hostSocket.next((f) => f.type === "joined");
      peerSocket.send({
        type: "offer",
        ice: { pwd: "0123456789abcdefghijkl", ufrag: "smok" },
        candidates: [
          { address: rb.address, kind: "relay", port: rb.port, priority: 100 },
          { address: "192.168.1.5", kind: "host", port: 27250, priority: 2000 },
        ],
      });
      const offer = await hostSocket.next((f) => f.type === "offer");
      check("a real relay address passes the deployed range list", offer.candidates.some((c) => c.kind === "relay" && c.address === rb.address));
      check("the host candidate is withheld", !offer.candidates.some((c) => c.kind === "host"));
      const notice = await peerSocket.next((f) => f.type === "notice");
      check("the peer is told what was withheld", notice.code === "candidates-withheld" && notice.withheld === 1);
      hostSocket.send({ type: "bye", peer: offer.peer, reason: "done" });
      await sleep(500);
      hostSocket.socket.close(); peerSocket.socket.close();

    } finally {
      await a.release(); await b.release();
    }
  }
} finally {
  const ended = await api("DELETE", `/v1/hosts/${host.serverId}`, { token });
  check("host session ended", ended.status === 204);
}
const failed = results.filter((r) => !r.ok).length;
console.log(`${results.length - failed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
