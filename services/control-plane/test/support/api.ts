/**
 * Calls the Worker the way a client would, plus host identities and registration built on the shared formats. Each
 * test passes its own address so rate-limit buckets never leak between tests.
 */
import { SELF } from "cloudflare:test";
import { stringify, type JsonObject, type JsonValue } from "../../src/shared/canonical-json";
import { concatBytes, parseHex, sha256Hex, toBase64Url, utf8 } from "../../src/shared/wire-text";

export const ORIGIN = "https://control-plane.example.invalid";

export interface ApiResult {
  readonly status: number;
  readonly headers: Headers;
  readonly text: string;
  readonly json: JsonObject;
}

export interface CallOptions {
  /** A JSON value is sent as canonical JSON; a string is sent as is. */
  readonly body?: JsonValue | string;
  readonly token?: string;
  readonly ip?: string;
  readonly headers?: Readonly<Record<string, string>>;
}

export async function call(method: string, path: string, options: CallOptions = {}): Promise<ApiResult> {
  const headers = new Headers(options.headers);
  if (options.ip !== undefined) headers.set("cf-connecting-ip", options.ip);
  if (options.token !== undefined) headers.set("authorization", `Bearer ${options.token}`);
  let body: string | undefined;
  if (options.body !== undefined) {
    body = typeof options.body === "string" ? options.body : stringify(options.body);
    if (!headers.has("content-type")) headers.set("content-type", "application/json");
  }
  const response = await SELF.fetch(`${ORIGIN}${path}`, { method, headers, ...(body === undefined ? {} : { body }) });
  const text = await response.text();
  let json: JsonObject = {};
  if (text.length > 0 && response.headers.get("content-type")?.startsWith("application/json")) json = JSON.parse(text) as JsonObject;
  return { status: response.status, headers: response.headers, text, json };
}

export interface HostIdentity {
  readonly label: string;
  readonly privateKey: CryptoKey;
  readonly signingKey: string;
  readonly transportKey: string;
  readonly serverId: string;
}

/** An Ed25519 identity from a 32-byte seed, imported the way the fixture generator does (PKCS #8). */
export async function identityFromSeed(label: string, seed: Uint8Array, transportKey?: Uint8Array): Promise<HostIdentity> {
  const pkcs8 = concatBytes(parseHex("302e020100300506032b657004220420", 16)!, seed);
  const privateKey = await crypto.subtle.importKey("pkcs8", pkcs8, { name: "Ed25519" }, true, ["sign"]);
  const jwk = (await crypto.subtle.exportKey("jwk", privateKey)) as JsonWebKey;
  const publicKey = Uint8Array.from(atob(jwk.x!.replace(/-/g, "+").replace(/_/g, "/") + "="), (character) => character.charCodeAt(0));
  return {
    label,
    privateKey,
    signingKey: toBase64Url(publicKey),
    transportKey: toBase64Url(transportKey ?? (await digest(`${label} transport`))),
    serverId: await sha256Hex(publicKey),
  };
}

/** A fresh identity derived from a label, so every test gets its own server. */
export async function identity(label: string): Promise<HostIdentity> {
  return identityFromSeed(label, await digest(`${label} signing`));
}

export async function sign(key: CryptoKey, message: Uint8Array): Promise<string> {
  return toBase64Url(new Uint8Array(await crypto.subtle.sign({ name: "Ed25519" }, key, message as Uint8Array<ArrayBuffer>)));
}

export interface DescriptorOptions {
  readonly name?: string;
  readonly visibility?: "public" | "invite" | "lan";
  readonly maxPlayers?: number;
  readonly gameBuild?: string;
  readonly protocolVersion?: string;
  readonly issuedAt?: number;
  readonly packages?: readonly JsonObject[];
}

/** A signed descriptor's canonical text, built exactly as tests/fixtures/multiplayer/generate.py builds them. */
export async function signedDescriptor(host: HostIdentity, options: DescriptorOptions = {}): Promise<string> {
  const packages = options.packages ?? [
    { id: "io.github.furroxide.topiaforge.multiplayer", version: "0.2.0", sha256: await sha256Hex(utf8("multiplayer archive")), role: "session-required" },
  ];
  const descriptor: JsonObject = {
    schema: "topiaforge.server-descriptor/1",
    serverId: host.serverId,
    signingKey: host.signingKey,
    transportKey: host.transportKey,
    issuedAt: options.issuedAt ?? 1893456000,
    name: options.name ?? `Server ${host.label}`,
    gameBuild: options.gameBuild ?? "2545",
    runtimeVersion: "0.2.0",
    protocolVersion: options.protocolVersion ?? "1.0.0",
    maxPlayers: options.maxPlayers ?? 8,
    passwordRequired: false,
    voice: true,
    visibility: options.visibility ?? "public",
    hostServesPackages: false,
    gamemode: "Open Sandbox",
    world: "Test City",
    packages,
    packageDigest: await sha256Hex(utf8(stringify(packages))),
  };
  const signature = await sign(host.privateKey, concatBytes(utf8("TopiaForge server descriptor v1\n"), utf8(stringify(descriptor))));
  return stringify({ descriptor, signature });
}

export interface Registered {
  readonly host: HostIdentity;
  readonly token: string;
  readonly descriptor: string;
  readonly response: ApiResult;
}

/** The registration body a host signs: everything it sends except the proof. */
export function registrationBody(challenge: string, descriptor: string, relayOnly: boolean): JsonObject {
  return { challenge, descriptor, relayOnly };
}

export async function registrationProof(host: HostIdentity, body: JsonObject): Promise<string> {
  return sign(host.privateKey, concatBytes(utf8("TopiaForge host registration v1\n"), utf8(stringify(body))));
}

/** Registers a host through the challenge and proof, and checks it worked. */
export async function register(
  host: HostIdentity,
  ip: string,
  options: DescriptorOptions & { readonly relayOnly?: boolean; readonly descriptor?: string } = {},
): Promise<Registered> {
  const descriptor = options.descriptor ?? (await signedDescriptor(host, options));
  const issued = await call("POST", "/v1/hosts/challenge", { ip, body: { signingKey: host.signingKey } });
  if (issued.status !== 200) throw new Error(`challenge failed: ${issued.status} ${issued.text}`);
  const body = registrationBody(issued.json["challenge"] as string, descriptor, options.relayOnly ?? false);
  const response = await call("POST", "/v1/hosts", { ip, body: { ...body, proof: await registrationProof(host, body) } });
  if (response.status !== 201) throw new Error(`registration failed: ${response.status} ${response.text}`);
  return { host, token: response.json["sessionToken"] as string, descriptor, response };
}

async function digest(text: string): Promise<Uint8Array<ArrayBuffer>> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", utf8(text)));
}
