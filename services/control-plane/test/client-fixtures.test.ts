/**
 * Records tests/fixtures/multiplayer/control-plane: exact HTTP exchanges and a signaling transcript that the game and
 * launcher clients replay. The clock, randomness and relay provider are fixed, so every byte is reproducible; the
 * committed files are Vitest file snapshots, and any drift in the wire format fails this test.
 *
 * Regenerate after a deliberate change with `npx vitest run -u test/client-fixtures.test.ts`.
 */
import { afterEach, describe, expect, it } from "vitest";
import descriptors from "../../../tests/fixtures/multiplayer/descriptor/cases.json";
import { stringify, type JsonValue } from "../src/shared/canonical-json";
import { parseBase64Url, parseHex } from "../src/shared/wire-text";
import { ORIGIN, call, identityFromSeed, registrationBody, registrationProof, signedDescriptor, type ApiResult } from "./support/api";
import { FakeRelayProvider } from "./support/relay";
import { restoreRuntime, useClock, useEntropy, useUpstream } from "./support/runtime";
import { connect, type SignalClient } from "./support/sockets";

const START = Date.UTC(2032, 5, 1);
const CALLER = "192.0.2.1";
const PEER = "198.51.100.77";
const RECORDED_RESPONSE_HEADERS = ["allow", "cache-control", "content-type", "retry-after", "x-content-type-options"];
const hostKey = descriptors.keys.find((key) => key.id === "host-a")!;
const sandbox = descriptors.valid.find((testCase) => testCase.id === "public-sandbox")!;

afterEach(() => restoreRuntime());

interface Exchange {
  readonly id: string;
  readonly about: string;
  readonly at: number;
  readonly request: { readonly method: string; readonly path: string; readonly headers: Record<string, string>; readonly body: string | null };
  readonly response: { readonly status: number; readonly headers: Record<string, string>; readonly body: string | null };
}

function setUp(seed: string) {
  const clock = useClock(START);
  useEntropy(seed);
  useUpstream(new FakeRelayProvider().handle);
  return clock;
}

async function host() {
  const transportKey = parseBase64Url(hostKey.transportKey, 32)!;
  return identityFromSeed("host-a", parseHex(hostKey.signingSeed, 32)!, transportKey);
}

describe("the client fixtures", () => {
  it("record every HTTP exchange byte for byte", async () => {
    const clock = setUp("control-plane client fixtures");
    const exchanges: Exchange[] = [];
    const record = async (id: string, about: string, method: string, path: string, options: { body?: JsonValue | string; token?: string } = {}): Promise<ApiResult> => {
      const body = options.body === undefined ? null : typeof options.body === "string" ? options.body : stringify(options.body);
      const response = await call(method, path, { ip: CALLER, ...(body === null ? {} : { body }), ...(options.token === undefined ? {} : { token: options.token }) });
      const requestHeaders: Record<string, string> = {};
      if (options.token !== undefined) requestHeaders["authorization"] = `Bearer ${options.token}`;
      if (body !== null) requestHeaders["content-type"] = "application/json";
      const responseHeaders: Record<string, string> = {};
      for (const name of RECORDED_RESPONSE_HEADERS) {
        const value = response.headers.get(name);
        if (value !== null) responseHeaders[name] = value;
      }
      exchanges.push({
        id,
        about,
        at: Math.floor(clock.now() / 1000),
        request: { method, path, headers: requestHeaders, body },
        response: { status: response.status, headers: responseHeaders, body: response.text.length > 0 ? response.text : null },
      });
      return response;
    };

    const identity = await host();
    await record("status", "What clients may use right now.", "GET", "/v1/status");
    const challenge = await record("host-challenge", "A host asks for a challenge for its signing key.", "POST", "/v1/hosts/challenge", { body: { signingKey: identity.signingKey } });
    const unsigned = registrationBody(challenge.json["challenge"] as string, sandbox.signedBytes, false);
    const registered = await record("host-register", "The host proves its key over the whole registration and gets a session token and an invite.", "POST", "/v1/hosts", {
      body: { ...unsigned, proof: await registrationProof(identity, unsigned) },
    });
    const token = registered.json["sessionToken"] as string;
    const serverId = registered.json["serverId"] as string;
    await record("host-heartbeat-too-soon", "Heartbeats closer than five seconds apart are refused.", "POST", `/v1/hosts/${serverId}/heartbeat`, { token, body: { players: 1 } });
    clock.advance(30);
    await record("host-heartbeat", "Every 30 seconds the host reports its player count and keeps its listing for 90 more.", "POST", `/v1/hosts/${serverId}/heartbeat`, { token, body: { players: 2 } });
    await record("host-view", "The host's own view of its session.", "GET", `/v1/hosts/${serverId}`, { token });
    const invite = await record("invite-create", "The host creates another invite.", "POST", `/v1/hosts/${serverId}/invites`, { token, body: { ttlSeconds: 3600 } });
    const code = (invite.json["invite"] as Record<string, string>)["code"]!;
    await record("servers-list", "The server browser's first page for one build and protocol major.", "GET", "/v1/servers?build=2545&protocol=1&limit=10");
    await record("server-detail", "A public server's listing and signed descriptor.", "GET", `/v1/servers/${serverId}`);
    await record("invite-resolve", "Invite codes read forgivingly: case, hyphens, O for 0 and I or L for 1.", "POST", "/v1/invites/resolve", {
      body: { code: `${code.slice(0, 5).toLowerCase()}-${code.slice(5)}` },
    });
    const ticket = await record("join-ticket", "A joining player asks for a ticket, choosing relay-only.", "POST", "/v1/join-tickets", { body: { serverId, invite: null, relayOnly: true } });
    await record("turn-credentials-joiner", "Relay credentials for the joiner, in the iceServers shape.", "POST", "/v1/turn/credentials", {
      token: ticket.json["ticket"] as string,
      body: { serverId },
    });
    await record("turn-credentials-host", "Relay credentials for the host.", "POST", "/v1/turn/credentials", { token, body: { serverId } });
    const updated = await signedDescriptor(identity, { name: "Charl's Sandbox", issuedAt: sandbox.descriptor.issuedAt + 60, maxPlayers: 8 });
    await record("host-descriptor", "The host publishes a newer signed descriptor.", "PUT", `/v1/hosts/${serverId}/descriptor`, { token, body: { descriptor: updated } });
    await record("invite-revoke", "The host withdraws an invite, naming it in the body so the code stays out of the URL.", "POST", `/v1/hosts/${serverId}/invites/revoke`, {
      token,
      body: { code },
    });
    await record("invite-resolve-miss", "Every miss is the same 404.", "POST", "/v1/invites/resolve", { body: { code } });
    await record("error-unauthorized", "A wrong or missing credential.", "POST", `/v1/hosts/${serverId}/heartbeat`, { token: `tfh1_${"A".repeat(43)}`, body: { players: 0 } });
    await record("error-malformed", "A body that is not canonical JSON.", "POST", "/v1/invites/resolve", { body: '{"code": "ABCDE12345"}' });
    await record("error-method", "A known path with the wrong method.", "PUT", "/v1/status", { body: {} });
    await record("error-unknown", "A path the API does not have.", "GET", "/v1/servers/unknown");
    await record("host-end", "The host ends its session.", "DELETE", `/v1/hosts/${serverId}`, { token });

    const document = {
      schema: 1,
      about:
        "Control-plane HTTP exchanges (docs/internal/ControlPlaneApi.md), recorded by services/control-plane/test/client-fixtures.test.ts with a fixed clock, seeded randomness and a stand-in relay provider. Bodies are exact canonical JSON text. A client must build each request body byte for byte and parse each response; tokens, codes and challenges are only valid against the recording.",
      origin: ORIGIN,
      host: { key: "host-a", descriptorCase: "public-sandbox", note: "Keys and the first descriptor come from tests/fixtures/multiplayer/descriptor." },
      exchanges,
    };
    await expect(`${JSON.stringify(document, null, 2)}\n`).toMatchFileSnapshot("../../../tests/fixtures/multiplayer/control-plane/exchanges.json");
  });

  it("record a signaling transcript, relay-only stripping included", async () => {
    const clock = setUp("control-plane signaling fixtures");
    const identity = await host();
    const issued = await call("POST", "/v1/hosts/challenge", { ip: CALLER, body: { signingKey: identity.signingKey } });
    const unsigned = registrationBody(issued.json["challenge"] as string, sandbox.signedBytes, false);
    const registered = await call("POST", "/v1/hosts", { ip: CALLER, body: { ...unsigned, proof: await registrationProof(identity, unsigned) } });
    const serverId = registered.json["serverId"] as string;
    const ticket = await call("POST", "/v1/join-tickets", { ip: PEER, body: { serverId, invite: null, relayOnly: true } });
    const transcript: { readonly step: number; readonly from: string; readonly to: string; readonly frame: string }[] = [];
    let step = 0;
    const receive = async (client: SignalClient, to: string) => {
      const frame = stringify(await client.next());
      transcript.push({ step: ++step, from: "service", to, frame });
    };
    const send = (client: SignalClient, from: string, frame: JsonValue) => {
      const text = stringify(frame);
      transcript.push({ step: ++step, from, to: "service", frame: text });
      client.send(text);
    };
    const hostClient = await connect(serverId, registered.json["sessionToken"] as string, CALLER);
    await receive(hostClient, "host");
    const peer = await connect(serverId, ticket.json["ticket"] as string, PEER);
    await receive(peer, "peer");
    await receive(hostClient, "host");
    const peerId = JSON.parse(transcript[1]!.frame)["peer"] as string;
    const ice = { ufrag: "abcd1234", pwd: "0123456789abcdefABCDEF" };
    send(peer, "peer", {
      type: "offer",
      ice,
      candidates: [
        { kind: "host", address: "192.168.1.20", port: 27250, priority: 2130706431 },
        { kind: "srflx", address: PEER, port: 40000, priority: 1694498815 },
        { kind: "relay", address: "141.101.90.10", port: 50000, priority: 16777215 },
        // Labelled a relay, but outside the relay provider's ranges: withheld like the others.
        { kind: "relay", address: "203.0.113.50", port: 50000, priority: 16777214 },
      ],
    });
    await receive(peer, "peer");
    await receive(hostClient, "host");
    send(hostClient, "host", {
      type: "answer",
      peer: peerId,
      ice: { ufrag: "wxyz5678", pwd: "ZYXWVUTSRQPONMLKJIHGFE" },
      candidates: [{ kind: "srflx", address: "203.0.113.9", port: 27250, priority: 1694498815 }],
    });
    await receive(peer, "peer");
    send(peer, "peer", { type: "candidate", candidate: { kind: "relay", address: "141.101.90.11", port: 50001, priority: 16777213 } });
    await receive(hostClient, "host");
    send(hostClient, "host", { type: "punch", peer: peerId, delayMs: 250 });
    await receive(peer, "peer");
    send(peer, "peer", { type: "bye", reason: "done" });
    await receive(hostClient, "host");
    await receive(hostClient, "host");
    hostClient.close();
    expect(clock.now()).toBe(START);

    const document = {
      schema: 1,
      about:
        "A signaling transcript (docs/internal/ControlPlaneApi.md, Signaling), recorded by services/control-plane/test/client-fixtures.test.ts. The joining peer chose relay-only, so the service forwards only its relay candidates inside the relay provider's address ranges, and withholds its host, server-reflexive and mislabelled candidates. Frames are exact canonical JSON text; a client must send its frames byte for byte and parse every frame it receives.",
      relayOnly: { host: false, peer: true },
      transcript,
    };
    await expect(`${JSON.stringify(document, null, 2)}\n`).toMatchFileSnapshot("../../../tests/fixtures/multiplayer/control-plane/signaling.json");
  });
});
