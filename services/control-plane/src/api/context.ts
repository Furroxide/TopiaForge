/**
 * What every handler needs about one request: the time it arrived, the derived keys, the caller's rate-limit bucket
 * and Durable Object stubs. The caller's address is read from CF-Connecting-IP into memory, turned into keyed hashes
 * of its prefix, and dropped with the request. It is never stored, logged or returned.
 */
import { readConfig, type Config } from "../config";
import type { Env } from "../env";
import type { DirectoryShard } from "../objects/directory-shard";
import type { HostSession } from "../objects/host-session";
import { inviteShardName, type InviteShard } from "../objects/invite-shard";
import type { LimitMode, RateLimiter } from "../objects/rate-limiter";
import type { Flags, SpendGuard } from "../objects/spend-guard";
import type { BucketKeys, WindowRule, WindowVerdict } from "../objects/windows";
import { hmac, hmacHex, serviceKeys, type ServiceKeys } from "../shared/keys";
import { ownScope, parseIp, prefixBucket, type IpAddress } from "../shared/ip";
import { clock } from "../shared/runtime";
import { utf8 } from "../shared/wire-text";
import { ApiError } from "./http";

const RATE_LIMITER_SHARDS = 64;
let flagCache: { readonly value: Flags; readonly fetchedAt: number } | null = null;

export class RequestContext {
  readonly at: number;
  readonly now: number;
  readonly url: URL;
  readonly config: Config;
  private keysPromise: Promise<ServiceKeys> | null | undefined;
  private bucketPromise: Promise<{ readonly keys: BucketKeys; readonly shard: number }> | undefined;

  constructor(
    readonly request: Request,
    readonly env: Env,
  ) {
    this.at = clock.now();
    this.now = Math.floor(this.at / 1000);
    this.url = new URL(request.url);
    this.config = readConfig(env);
  }

  /** The derived service keys; the service is unavailable without SERVICE_KEY. */
  keys(): Promise<ServiceKeys> {
    if (this.keysPromise === undefined) this.keysPromise = serviceKeys(this.env.SERVICE_KEY);
    if (this.keysPromise === null) throw new ApiError("service-unavailable");
    return this.keysPromise;
  }

  hasServiceKey(): boolean {
    if (this.keysPromise === undefined) this.keysPromise = serviceKeys(this.env.SERVICE_KEY);
    return this.keysPromise !== null;
  }

  /** The caller's address, in memory only, or null when Cloudflare did not supply one. */
  clientAddress(): IpAddress | null {
    return parseIp(this.request.headers.get("cf-connecting-ip"));
  }

  /**
   * The keyed fingerprint of the caller's own address scope (its IPv4 address or its IPv6 /64), used only to recognize
   * it in the caller's own signaling candidates.
   */
  async selfFingerprint(): Promise<string | null> {
    const address = this.clientAddress();
    return address === null ? null : hmacHex((await this.keys()).self, ownScope(address));
  }

  /** The keyed fingerprint of the caller's /24 or /56, which a signaling room counts its sockets by. */
  async sourceFingerprint(): Promise<string> {
    const address = this.clientAddress();
    return hmacHex((await this.keys()).self, `source v1|${address === null ? "unknown" : prefixBucket(address)}`);
  }

  /** This hour's and the previous hour's rate-limit keys for the caller's /24 or /56, and its limiter shard. */
  bucket(): Promise<{ readonly keys: BucketKeys; readonly shard: number }> {
    if (this.bucketPromise === undefined) {
      this.bucketPromise = (async () => {
        const keys = await this.keys();
        const address = this.clientAddress();
        const bucket = address === null ? "unknown" : prefixBucket(address);
        const hour = Math.floor(this.now / 3600);
        const [current, previous, shard] = await Promise.all([
          hmacHex(keys.rate, `rate v1|${bucket}|${hour}`),
          hmacHex(keys.rate, `rate v1|${bucket}|${hour - 1}`),
          hmac(keys.rate, utf8(`shard v1|${bucket}`)),
        ]);
        return { keys: { hour, current, previous }, shard: shard[0]! % RATE_LIMITER_SHARDS };
      })();
    }
    return this.bucketPromise;
  }

  /** Counts one request against a per-prefix limit, in the same step as the check; throws `rate-limited` when it refuses. */
  async limit(scope: string, rules: readonly WindowRule[]): Promise<void> {
    const verdict = await this.limiter(scope, rules, "consume");
    if (!verdict.allowed) throw new ApiError("rate-limited", verdict.retryAfterSeconds);
  }

  /** Gives back one request this request counted with `limit`, in the same windows. */
  async refund(scope: string, rules: readonly WindowRule[]): Promise<void> {
    await this.limiter(scope, rules, "refund");
  }

  private async limiter(scope: string, rules: readonly WindowRule[], mode: LimitMode): Promise<WindowVerdict> {
    const { keys, shard } = await this.bucket();
    const stub = this.env.RATE_LIMITER.get(this.env.RATE_LIMITER.idFromName(`rate/${shard}`)) as DurableObjectStub<RateLimiter>;
    return stub.limit({ scope, keys, rules, at: this.at, mode });
  }

  /** Feature flags and the kill switch, cached in the isolate for FLAG_CACHE_SECONDS. */
  async flags(): Promise<Flags> {
    if (flagCache !== null && this.at - flagCache.fetchedAt < this.config.flagCacheMilliseconds && this.at >= flagCache.fetchedAt) {
      return flagCache.value;
    }
    const value = await this.spendGuard().flags();
    flagCache = { value, fetchedAt: this.at };
    return value;
  }

  rememberFlags(value: Flags): void {
    flagCache = { value, fetchedAt: this.at };
  }

  async requireFeature(feature: keyof Flags["features"]): Promise<void> {
    if (!(await this.flags()).features[feature]) throw new ApiError("feature-disabled");
  }

  hostSession(serverId: string): DurableObjectStub<HostSession> {
    return this.env.HOST_SESSIONS.get(this.env.HOST_SESSIONS.idFromName(serverId));
  }

  spendGuard(): DurableObjectStub<SpendGuard> {
    return this.env.SPEND_GUARD.get(this.env.SPEND_GUARD.idFromName("spend-guard"));
  }

  directoryShard(name: string): DurableObjectStub<DirectoryShard> {
    return this.env.DIRECTORY_SHARDS.get(this.env.DIRECTORY_SHARDS.idFromName(name));
  }

  inviteShard(code: string): DurableObjectStub<InviteShard> {
    return this.env.INVITE_SHARDS.get(this.env.INVITE_SHARDS.idFromName(inviteShardName(code)));
  }
}

/** Forgets cached flags; tests call this between cases. */
export function clearFlagCache(): void {
  flagCache = null;
}
