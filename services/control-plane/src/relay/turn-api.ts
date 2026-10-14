/**
 * Cloudflare Realtime TURN: minting and revoking short-lived credentials. The provider's answer is untrusted input,
 * so it is size-bounded, shape-checked and filtered before anything of it reaches a client: only STUN and TURN URLs
 * that name a host by DNS name survive, so a response never carries an IP address.
 */
import type { RelayProvider } from "../config";
import { upstream } from "../shared/runtime";

const TURN_ORIGIN = "https://rtc.live.cloudflare.com";
const MAXIMUM_RESPONSE_BYTES = 16 * 1024;
const TIMEOUT_MILLISECONDS = 5000;
const URL_PATTERN = /^(stun|turns?):([a-z0-9-]+(?:\.[a-z0-9-]+)+)(?::([0-9]{1,5}))?(?:\?transport=(?:udp|tcp))?$/;
const SECRET_PATTERN = /^[\x21-\x7e]{1,512}$/;

export interface IceServer {
  readonly urls: readonly string[];
  readonly username?: string;
  readonly credential?: string;
}

export interface MintedCredentials {
  readonly iceServers: readonly IceServer[];
  /** The TURN username, which the usage analytics report against and revocation names. */
  readonly username: string;
}

export class UpstreamError extends Error {
  override readonly name = "UpstreamError";
}

export async function generateIceServers(provider: RelayProvider, ttlSeconds: number): Promise<MintedCredentials> {
  const response = await call(
    new Request(`${TURN_ORIGIN}/v1/turn/keys/${encodeURIComponent(provider.keyId)}/credentials/generate-ice-servers`, {
      method: "POST",
      headers: { authorization: `Bearer ${provider.apiToken}`, "content-type": "application/json" },
      body: JSON.stringify({ ttl: ttlSeconds }),
    }),
  );
  if (response.status !== 200 && response.status !== 201) throw new UpstreamError(`status ${response.status}`);
  return readIceServers(await boundedJson(response, MAXIMUM_RESPONSE_BYTES));
}

/** Revokes one credential. True once the provider confirms. */
export async function revokeCredential(provider: RelayProvider, username: string): Promise<boolean> {
  if (!SECRET_PATTERN.test(username)) return false;
  try {
    const response = await call(
      new Request(
        `${TURN_ORIGIN}/v1/turn/keys/${encodeURIComponent(provider.keyId)}/credentials/${encodeURIComponent(username)}/revoke`,
        { method: "POST", headers: { authorization: `Bearer ${provider.apiToken}` } },
      ),
    );
    await response.body?.cancel();
    return response.status === 204 || response.status === 200 || response.status === 404;
  } catch {
    return false;
  }
}

export function readIceServers(value: unknown): MintedCredentials {
  if (!isRecord(value) || !Array.isArray(value["iceServers"])) throw new UpstreamError("no iceServers");
  const entries = value["iceServers"] as unknown[];
  if (entries.length === 0 || entries.length > 8) throw new UpstreamError("iceServers count");
  const servers: IceServer[] = [];
  let username: string | null = null;
  for (const entry of entries) {
    if (!isRecord(entry) || !Array.isArray(entry["urls"])) throw new UpstreamError("entry shape");
    const urls = (entry["urls"] as unknown[]).filter((url): url is string => typeof url === "string" && isServiceUrl(url));
    if ((entry["urls"] as unknown[]).length > 16) throw new UpstreamError("too many urls");
    const entryUsername = entry["username"];
    const credential = entry["credential"];
    if (entryUsername === undefined && credential === undefined) {
      if (urls.length > 0) servers.push({ urls });
      continue;
    }
    if (typeof entryUsername !== "string" || typeof credential !== "string" || !SECRET_PATTERN.test(entryUsername) || !SECRET_PATTERN.test(credential)) {
      throw new UpstreamError("credential shape");
    }
    if (username !== null) throw new UpstreamError("more than one credential");
    const relayUrls = urls.filter((url) => !url.startsWith("stun:"));
    if (relayUrls.length === 0) throw new UpstreamError("no usable relay url");
    username = entryUsername;
    servers.push({ urls: relayUrls, username: entryUsername, credential });
  }
  if (username === null) throw new UpstreamError("no credential");
  return { iceServers: servers, username };
}

function isServiceUrl(url: string): boolean {
  const match = URL_PATTERN.exec(url);
  if (match === null) return false;
  const host = match[2]!;
  // A DNS name, never an address: the last label must hold a letter.
  const last = host.slice(host.lastIndexOf(".") + 1);
  if (!/[a-z]/.test(last)) return false;
  const port = match[3];
  return port === undefined || (Number(port) >= 1 && Number(port) <= 65535);
}

/** Sends one request with a hard deadline; any failure becomes an UpstreamError that names no detail. */
export async function call(request: Request): Promise<Response> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new UpstreamError("timeout")), TIMEOUT_MILLISECONDS);
  });
  try {
    return await Promise.race([upstream.fetch(new Request(request, { signal: AbortSignal.timeout(TIMEOUT_MILLISECONDS) })), deadline]);
  } catch (error) {
    throw error instanceof UpstreamError ? error : new UpstreamError("unreachable");
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

/** Reads at most `maximumBytes` of a JSON response body. */
export async function boundedJson(response: Response, maximumBytes: number): Promise<unknown> {
  const declared = response.headers.get("content-length");
  if (declared !== null && Number(declared) > maximumBytes) throw new UpstreamError("response too large");
  if (response.body === null) throw new UpstreamError("empty response");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maximumBytes) {
      await reader.cancel();
      throw new UpstreamError("response too large");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  try {
    return JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes)) as unknown;
  } catch {
    throw new UpstreamError("malformed response");
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
