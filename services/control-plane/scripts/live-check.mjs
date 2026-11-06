// End-to-end check of a deployed control plane, as a host and a joining player would use it.
//
//   node scripts/live-check.mjs https://mp-staging.topiaforge.dev
//
// It talks only to the deployments this repository defines, or to `wrangler dev` on this machine, and reads its
// fixtures and wrangler.toml from the checkout it lives in.
//
// It uses a freshly generated, invite-only test server named "TopiaForge staging check", so nothing is listed
// publicly and no fixture data reaches the service. Tokens, tickets and relay credentials stay inside this process:
// only their shapes are printed. The player's own public address, which TURN reports back, is never printed.
import crypto from "node:crypto";
import { TurnAllocation } from "./turn-probe.mjs";
import dns from "node:dns/promises";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const ORIGINS = new Map([
  ["https://mp-staging.topiaforge.dev", "https://mp-staging.topiaforge.dev"],
  ["https://mp.topiaforge.dev", "https://mp.topiaforge.dev"],
  ["http://127.0.0.1:8787", "http://127.0.0.1:8787"],
  ["http://localhost:8787", "http://localhost:8787"],
]);
// The request target is always one of the constants above, never the argument itself.
const origin = ORIGINS.get(process.argv[2] ?? "");
if (!origin) throw new Error(`usage: node scripts/live-check.mjs <${[...ORIGINS.keys()].join("|")}>`);
const repo = fileURLToPath(new URL("../../../", import.meta.url));
// The service's own dev dependencies carry the ws client, which can send an Authorization header.
const WebSocketClient = createRequire(path.join(repo, "services/control-plane/package.json"))("ws");

const results = [];
function check(name, ok, detail = "") {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} ${name}${detail ? ` (${detail})` : ""}`);
}
const shape = (token) => (typeof token === "string" ? `${token.length} chars` : String(token));

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
      check("relay addresses are IPv4", ra.family === 4 && rb.family === 4, "two IPv4 relay allocations");
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
