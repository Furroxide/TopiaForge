/** Aggregate Internet activity only. No descriptors, addresses, candidates or player identities leave a host. */
import { MEASUREMENT_INTERVAL_MINUTES } from "./config";
import type { Env } from "./env";
import type { SessionRow } from "./objects/host-store";
import { clock, nowSeconds } from "./shared/runtime";

export const ACTIVITY_SHARDS = 16;
/** The one cron: activity collection and the relay-spend measurement, every MEASUREMENT_INTERVAL_MINUTES. */
export const ACTIVITY_CRON = `*/${MEASUREMENT_INTERVAL_MINUTES} * * * *`;
export interface ActivityLease {
  readonly serverId: string;
  readonly version: number;
  readonly expiresAt: number;
  readonly players: number;
}
export function monitorIds(env: Env): readonly string[] {
  return (env.MONITOR_SERVER_IDS ?? "")
    .split(/\s+/)
    .filter((value) => /^[0-9a-f]{64}$/.test(value));
}
export function activityEnabled(env: Env): boolean {
  return (
    typeof env.MONITOR_ACTIVITY_TOKEN === "string" &&
    env.MONITOR_ACTIVITY_TOKEN.length >= 32 &&
    monitorIds(env).length > 0
  );
}
interface Pending extends Record<string, SqlStorageValue> {
  server_id: string;
  version: number;
  expires_at: number;
  players: number;
  attempts: number;
  retry_at: number;
  keep_until: number;
}
/** One coalescing durable outbox per host. Analytics never joins the host's success/failure path. */
export class ActivityReporter {
  private pending: Promise<void> = Promise.resolve();
  constructor(
    private readonly ctx: DurableObjectState,
    private readonly env: Env,
  ) {}
  private ensure(): void {
    this.ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS activity_outbox (id INTEGER PRIMARY KEY, server_id TEXT, version INTEGER, expires_at INTEGER, players INTEGER, attempts INTEGER, retry_at INTEGER, keep_until INTEGER)",
    );
  }
  record(session: SessionRow, ended = false): void {
    if (!activityEnabled(this.env)) return;
    try {
      this.ensure();
      const previous = this.ctx.storage.sql
        .exec<Pending>("SELECT * FROM activity_outbox WHERE id = 1")
        .toArray()[0];
      const version = Math.max(clock.now(), (previous?.version ?? 0) + 1);
      const expires =
        ended || monitorIds(this.env).includes(session.serverId)
          ? 0
          : session.expiresAt;
      this.ctx.storage.sql.exec(
        "INSERT OR REPLACE INTO activity_outbox VALUES (1,?,?,?,?,0,?,?)",
        session.serverId,
        version,
        expires,
        session.players,
        nowSeconds(),
        nowSeconds() + 90,
      );
      this.pending = this.pending
        .then(
          () => this.flush(),
          () => this.flush(),
        )
        .catch(() => undefined);
      this.ctx.waitUntil(this.pending);
    } catch {
      /* An unavailable analytics store must never refuse a host operation. */
    }
  }
  settled(): Promise<void> {
    return this.pending;
  }
  async flush(): Promise<void> {
    if (!activityEnabled(this.env)) return;
    this.ensure();
    const row = this.ctx.storage.sql
      .exec<Pending>("SELECT * FROM activity_outbox WHERE id = 1")
      .toArray()[0];
    if (
      !row ||
      row.attempts >= 3 ||
      row.retry_at > nowSeconds() ||
      row.keep_until <= nowSeconds()
    )
      return;
    // Reserve the attempt before yielding. A replacement lease supersedes this attempt atomically.
    this.ctx.storage.sql.exec(
      "UPDATE activity_outbox SET attempts = attempts + 1, retry_at = ? WHERE version = ?",
      nowSeconds() + [5, 15, 30][row.attempts]!,
      row.version,
    );
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const shard = this.env.ACTIVITY_SHARDS.get(
        this.env.ACTIVITY_SHARDS.idFromName(
          `activity/${parseInt(row.server_id[0]!, 16)}`,
        ),
      );
      await Promise.race([
        shard.apply({
          serverId: row.server_id,
          version: row.version,
          expiresAt: row.expires_at,
          players: row.players,
        }),
        new Promise<never>((_resolve, reject) => {
          timer = setTimeout(
            () => reject(new Error("analytics deadline")),
            2000,
          );
        }),
      ]);
      this.ctx.storage.sql.exec(
        "UPDATE activity_outbox SET attempts = 3 WHERE version = ?",
        row.version,
      );
    } finally {
      if (timer !== undefined) clearTimeout(timer);
      const next = this.nextDue();
      if (next !== null) {
        const current = await this.ctx.storage.getAlarm();
        if (current === null || current > next * 1000)
          await this.ctx.storage.setAlarm(next * 1000);
      }
    }
  }
  nextDue(): number | null {
    if (!activityEnabled(this.env)) return null;
    try {
      this.ensure();
      this.ctx.storage.sql.exec(
        "DELETE FROM activity_outbox WHERE keep_until <= ?",
        nowSeconds(),
      );
      const row = this.ctx.storage.sql
        .exec<Pending>("SELECT * FROM activity_outbox WHERE id = 1")
        .toArray()[0];
      return row
        ? row.attempts >= 3
          ? row.keep_until
          : Math.min(row.keep_until, row.retry_at)
        : null;
    } catch {
      return null;
    }
  }
}
