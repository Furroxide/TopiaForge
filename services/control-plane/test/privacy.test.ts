/**
 * Privacy as properties. Seeded random walks drive the whole HTTP API from IPv4, IPv6 and IPv4-mapped callers, and
 * every response body and header is checked for anything that reads as an IP address or names an address field.
 * Afterwards every row of every Durable Object's storage is checked for the callers' addresses and prefixes.
 */
import { listDurableObjectIds, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterAll, afterEach, beforeEach, describe, expect, it } from "vitest";
import { parseIp, formatIp, isLocalOnly, prefixBucket } from "../src/shared/ip";
import { permittedCandidates, type Candidate } from "../src/shared/signaling";
import { call, identity, register, signedDescriptor, type ApiResult, type HostIdentity } from "./support/api";
import { seededBytes } from "./support/runtime";
import { endScenario, startScenario, type Scenario } from "./support/scenario";

const FORBIDDEN_KEYS = new Set(["address", "addresses", "port", "ports", "ip", "ips", "candidate", "candidates", "endpoint", "host"]);
const RUNS = 12;
const STEPS = 30;
const ADMIN_TOKEN = "test-only-admin-token-not-a-real-secret";
const callers = new Set<string>();

/** Every substring of `text` that reads as an IP address. */
export function addressesIn(text: string): string[] {
  const found = [...text.matchAll(/(?<![0-9])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9])/g)].map((match) => match[0]);
  for (const match of text.matchAll(/[0-9A-Fa-f:]{3,}/g)) {
    const run = match[0];
    if ((run.match(/:/g) ?? []).length >= 2 && parseIp(run.replace(/^:(?!:)|(?<!:):$/g, "")) !== null) found.push(run);
  }
  return found;
}

function forbiddenKeys(value: unknown, path = "$"): string[] {
  if (Array.isArray(value)) return value.flatMap((item, index) => forbiddenKeys(item, `${path}[${index}]`));
  if (typeof value !== "object" || value === null) return [];
  return Object.entries(value).flatMap(([key, item]) => [...(FORBIDDEN_KEYS.has(key) ? [`${path}.${key}`] : []), ...forbiddenKeys(item, `${path}.${key}`)]);
}

function checkResponse(response: ApiResult, label: string): void {
  expect(addressesIn(response.text), `${label}: body`).toEqual([]);
  for (const [name, value] of response.headers) expect(addressesIn(value), `${label}: header ${name}`).toEqual([]);
  if (response.text.length > 0) expect(forbiddenKeys(JSON.parse(response.text)), `${label}: keys`).toEqual([]);
  for (const caller of callers) expect(response.text.includes(caller), `${label}: echoes a caller`).toBe(false);
}

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("privacy");
});
afterEach(() => endScenario());

describe("no response ever carries an address", () => {
  for (let run = 0; run < RUNS; run++) {
    it(`holds for random walk ${run}`, async () => {
      const random = seededBytes(`privacy walk ${run}`);
      const pick = <T>(items: readonly T[]): T => items[random(1)[0]! % items.length]!;
      const octet = () => (random(1)[0]! % 254) + 1;
      const v4 = () => `${pick([45, 81, 141, 172, 203])}.${octet()}.${octet()}.${octet()}`;
      const v6 = () => formatIp({ family: 6, bytes: Uint8Array.of(0x26, 0x06, ...random(14)) });
      const addresses = [v4(), v6(), `::ffff:${v4()}`, v4()];
      for (const address of addresses) {
        callers.add(address);
        callers.add(formatIp(parseIp(address)!));
      }
      const hosts: { identity: HostIdentity; token: string; code: string; issuedAt: number }[] = [];
      const tickets: { serverId: string; ticket: string }[] = [];
      let successes = 0;
      for (let index = 0; index < 1 + (random(1)[0]! % 3); index++) {
        const host = await identity(`privacy-${run}-${index}`);
        const registered = await register(host, pick(addresses), { visibility: pick(["public", "invite"] as const), relayOnly: random(1)[0]! % 2 === 0, issuedAt: 1000 });
        checkResponse(registered.response, "registration");
        hosts.push({ identity: host, token: registered.token, code: (registered.response.json["invite"] as Record<string, string>)["code"]!, issuedAt: 1000 });
      }
      for (let step = 0; step < STEPS; step++) {
        scenario.clock.advance(random(1)[0]! % 12);
        const host = pick(hosts);
        const ip = pick(addresses);
        const serverId = host.identity.serverId;
        const operations: (() => Promise<ApiResult>)[] = [
          () => call("GET", "/v1/status", { ip }),
          () => call("GET", "/v1/servers?build=2545&protocol=1", { ip }),
          () => call("GET", `/v1/servers/${serverId}`, { ip }),
          () => call("POST", `/v1/hosts/${serverId}/heartbeat`, { ip, token: host.token, body: { players: random(1)[0]! % 9 } }),
          () => call("GET", `/v1/hosts/${serverId}`, { ip, token: host.token }),
          () => call("POST", `/v1/hosts/${serverId}/invites`, { ip, token: host.token, body: { ttlSeconds: 600 } }),
          () => call("POST", "/v1/invites/resolve", { ip, body: { code: pick([host.code, "ZZZZZZZZZZ", "not a code"]) } }),
          async () => {
            const response = await call("POST", "/v1/join-tickets", { ip, body: { serverId, invite: pick([host.code, null]), relayOnly: random(1)[0]! % 2 === 0 } });
            if (response.status === 201) tickets.push({ serverId, ticket: response.json["ticket"] as string });
            return response;
          },
          () => call("POST", "/v1/turn/credentials", { ip, token: host.token, body: { serverId } }),
          () => {
            const joiner = tickets.length > 0 ? pick(tickets) : null;
            return call("POST", "/v1/turn/credentials", { ip, token: joiner?.ticket ?? "tfj1_x", body: { serverId: joiner?.serverId ?? serverId } });
          },
          async () => {
            host.issuedAt += 1;
            const descriptor = await signedDescriptor(host.identity, { issuedAt: host.issuedAt, visibility: pick(["public", "invite"] as const) });
            return call("PUT", `/v1/hosts/${serverId}/descriptor`, { ip, token: host.token, body: { descriptor } });
          },
          () => call("POST", "/v1/hosts", { ip, body: pick(['{"challenge": 1}', "[]", '"x"', "{}"]) }),
          () => call("GET", `/v1/nowhere/${ip}`, { ip }),
          () => call("GET", "/v1/admin/state", { ip, token: ADMIN_TOKEN }),
          () => call("DELETE", `/v1/hosts/${serverId}/invites/ZZZZZZZZZZ`, { ip, token: host.token }),
        ];
        const response = await pick(operations)();
        checkResponse(response, `walk ${run} step ${step}`);
        if (response.status < 300) successes++;
      }
      // A walk of nothing but refusals would prove nothing.
      expect(successes, "successful calls in the walk").toBeGreaterThanOrEqual(STEPS / 3);
    });
  }
});

describe("no stored byte names a caller", () => {
  afterAll(() => callers.clear());

  it("holds across every Durable Object the walks touched", async () => {
    expect(callers.size).toBeGreaterThan(0);
    const prefixes = [...callers].flatMap((caller) => {
      const address = parseIp(caller)!;
      const bucket = prefixBucket(address);
      return [bucket, bucket.slice(3)];
    });
    let rows = 0;
    for (const namespace of [env.HOST_SESSIONS, env.DIRECTORY_SHARDS, env.INVITE_SHARDS, env.SPEND_GUARD, env.RATE_LIMITER] as DurableObjectNamespace[]) {
      for (const id of await listDurableObjectIds(namespace)) {
        const dump = await runInDurableObject(namespace.get(id), (_, state) => {
          const tables = state.storage.sql.exec<{ name: string }>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite%' AND name NOT LIKE '\\_cf%' ESCAPE '\\'").toArray();
          return tables.map(({ name }) => JSON.stringify(state.storage.sql.exec(`SELECT * FROM "${name}"`).toArray()));
        });
        for (const table of dump) {
          rows++;
          expect(addressesIn(table)).toEqual([]);
          for (const text of [...callers, ...prefixes]) expect(table.includes(text), `storage holds ${text}`).toBe(false);
        }
      }
    }
    expect(rows).toBeGreaterThan(0);
  });
});

describe("relay-only candidates", () => {
  it("only ever keep public relay candidates that are not the sender's own address", async () => {
    const random = seededBytes("candidate property");
    const own = "198.51.100.77";
    const pool = ["192.168.1.20", "10.0.0.5", "100.64.1.1", "fe80::1", "fd00::5", "::1", own, "203.0.113.50", "2001:db8::50", "141.101.90.1"];
    const fingerprint = async (address: string) => `fp:${address}`;
    for (let trial = 0; trial < 500; trial++) {
      const candidates: Candidate[] = Array.from({ length: random(1)[0]! % 17 }, () => ({
        kind: (["host", "srflx", "relay"] as const)[random(1)[0]! % 3]!,
        address: pool[random(1)[0]! % pool.length]!,
        port: 1 + (random(1)[0]! % 65535),
        priority: random(1)[0]!,
      }));
      const kept = await permittedCandidates(candidates, { relayOnly: true, selfFingerprint: `fp:${own}` }, fingerprint);
      for (const candidate of kept) {
        expect(candidate.kind).toBe("relay");
        expect(isLocalOnly(parseIp(candidate.address)!)).toBe(false);
        expect(candidate.address).not.toBe(own);
      }
      expect(kept.length).toBe(candidates.filter((c) => c.kind === "relay" && !isLocalOnly(parseIp(c.address)!) && c.address !== own).length);
      expect(await permittedCandidates(candidates, { relayOnly: false, selfFingerprint: `fp:${own}` }, fingerprint)).toEqual(candidates);
    }
  });
});

describe("the address detector itself", () => {
  it("finds addresses in every spelling and nothing in the service's ordinary output", () => {
    for (const text of ["at 203.0.113.9 now", "2001:db8::1", "x[2001:db8:0:0:0:0:0:1]y", "::ffff:198.51.100.2", "fe80::1%eth0", "ip=10.0.0.1:443"]) {
      expect(addressesIn(text), text).not.toEqual([]);
    }
    for (const text of ['{"expiresAt":1893456000,"version":"0.2.0-rc.1+build.7"}', "turn:turn.cloudflare.com:3478?transport=udp", "tfh1_abc-DEF_123", "a".repeat(64)]) {
      expect(addressesIn(text), text).toEqual([]);
    }
  });
});
