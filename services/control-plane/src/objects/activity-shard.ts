import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import type { ActivityLease } from "../activity";
import { clock, nowSeconds } from "../shared/runtime";
import { MAXIMUM_PLAYERS } from "../shared/descriptor";

/** Expiring internal session keys provide idempotency, never a public session catalogue. */
export class ActivityShard extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS lease (server_id TEXT PRIMARY KEY, version INTEGER NOT NULL, expires_at INTEGER NOT NULL, players INTEGER NOT NULL, forget_at INTEGER NOT NULL)",
    );
    ctx.storage.sql.exec(
      "CREATE INDEX IF NOT EXISTS lease_expiry ON lease(forget_at)",
    );
  }
  async apply(value: ActivityLease): Promise<void> {
    if (
      !/^[0-9a-f]{64}$/.test(value.serverId) ||
      !Number.isSafeInteger(value.version) ||
      value.version > clock.now() + 1000 ||
      !Number.isSafeInteger(value.expiresAt) ||
      value.expiresAt > nowSeconds() + 90 ||
      !Number.isSafeInteger(value.players) ||
      value.players < 0 ||
      value.players > MAXIMUM_PLAYERS
    )
      throw new Error("invalid lease");
    this.ctx.storage.sql.exec(
      "DELETE FROM lease WHERE forget_at <= ?",
      nowSeconds(),
    );
    // Retain tombstones until every bounded outbox retry has expired, preventing resurrection after an end.
    this.ctx.storage.sql.exec(
      `INSERT INTO lease VALUES (?,?,?,?,?) ON CONFLICT(server_id) DO UPDATE SET
      version=excluded.version,expires_at=excluded.expires_at,players=excluded.players,forget_at=excluded.forget_at
      WHERE excluded.version > lease.version`,
      value.serverId,
      value.version,
      value.expiresAt,
      value.players,
      nowSeconds() + 180,
    );
    await this.ctx.storage.setAlarm(clock.now() + 180000);
  }
  totals(at: number): { sessions: number; players: number } {
    const row = this.ctx.storage.sql
      .exec<{
        sessions: number;
        players: number;
      }>("SELECT COUNT(*) AS sessions,COALESCE(SUM(players),0) AS players FROM lease WHERE expires_at > ?", at)
      .one();
    return { sessions: row.sessions, players: row.players };
  }
  override async alarm(): Promise<void> {
    this.ctx.storage.sql.exec(
      "DELETE FROM lease WHERE forget_at <= ?",
      nowSeconds(),
    );
    const next = this.ctx.storage.sql
      .exec<{ at: number | null }>("SELECT MIN(forget_at) AS at FROM lease")
      .one().at;
    if (next !== null)
      await this.ctx.storage.setAlarm(Math.max(clock.now(), next * 1000));
  }
}
