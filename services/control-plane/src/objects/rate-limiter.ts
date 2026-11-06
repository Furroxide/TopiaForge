/**
 * RateLimiter: per-address-prefix limits for everything except relay minting, which the spend guard limits itself.
 *
 * Instances are sharded 64 ways by a keyed hash of the prefix, so one prefix always lands on one instance. The
 * instance never sees an address or a prefix: only hourly keys (keys.ts) and window counts, each kept at most two
 * hours (windows.ts). An alarm deletes rows as they expire.
 */
import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { clock, nowSeconds } from "../shared/runtime";
import { ensureCounterTable, evaluate, increment, nextCounterExpiry, purgeCounters, type BucketKeys, type WindowRule, type WindowVerdict } from "./windows";

export type LimitMode = "consume" | "refund";

export interface LimitRequest {
  readonly scope: string;
  readonly keys: BucketKeys;
  readonly rules: readonly WindowRule[];
  /** The time the keys were derived for. */
  readonly at: number;
  /**
   * `consume` counts one event if it fits under every rule, in the same step as the check, so concurrent requests can
   * never all fit. `refund` gives one back: an invite lookup holds a miss slot while it runs and returns it on a hit.
   */
  readonly mode: LimitMode;
}

export class RateLimiter extends DurableObject<Env> {
  private readonly sql: SqlStorage;
  private alarmAt: number | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    ensureCounterTable(this.sql);
  }

  async limit(request: LimitRequest): Promise<WindowVerdict> {
    purgeCounters(this.sql, nowSeconds());
    if (request.mode === "refund") {
      increment(this.sql, request.scope, request.keys, request.rules, -1, request.at);
      return { allowed: true, retryAfterSeconds: 0 };
    }
    const verdict = evaluate(this.sql, request.scope, request.keys, request.rules, 1, request.at);
    if (verdict.allowed) {
      increment(this.sql, request.scope, request.keys, request.rules, 1, request.at);
      await this.scheduleCleanup();
    }
    return verdict;
  }

  override async alarm(): Promise<void> {
    this.alarmAt = null;
    purgeCounters(this.sql, nowSeconds());
    await this.scheduleCleanup();
  }

  private async scheduleCleanup(): Promise<void> {
    const next = nextCounterExpiry(this.sql);
    if (next === null) return;
    const at = Math.max(next * 1000, clock.now());
    if (this.alarmAt !== null && this.alarmAt <= at) return;
    this.alarmAt = at;
    await this.ctx.storage.setAlarm(at);
  }
}
