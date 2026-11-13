/** Public Internet activity only. No descriptors, addresses, candidates or player identities leave a host. */
import type { Env } from "./env";
import type { SessionRow } from "./objects/host-store";
import { clock, nowSeconds } from "./shared/runtime";

export const ACTIVITY_SHARDS = 16;
export const ACTIVITY_CRON = "*/5 * * * *";
/** The one activity format this service publishes: delayed public-only bands. */
export const ACTIVITY_SCHEMA = "topiaforge.activity/2";
export interface ActivityLease {
  readonly scope: "public-v2";
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
/**
 * Whether activity is collected and published. Configured monitor secrets turn it on only for the public-only band
 * format (`schema`, the format this code publishes): exact or combined totals are never published, whatever is set.
 */
export function activityEnabled(env: Env, schema: string = ACTIVITY_SCHEMA): boolean {
  return (
    schema === "topiaforge.activity/2" &&
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
  private ready = false;
  constructor(
    private readonly ctx: DurableObjectState,
    private readonly env: Env,
  ) {}
  /** Creates the outbox once per object, and again after the host wipes its storage (see `wiped`). */
  private ensure(): void {
    if (this.ready) return;
    this.ctx.storage.sql.exec("DROP TABLE IF EXISTS activity_outbox");
    this.ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS public_activity_outbox_v2 (id INTEGER PRIMARY KEY, server_id TEXT, version INTEGER, expires_at INTEGER, players INTEGER, attempts INTEGER, retry_at INTEGER, keep_until INTEGER)",
    );
    this.ready = true;
  }
  /** Forgets that the outbox exists, after the host's storage was wiped. */
  wiped(): void {
    this.ready = false;
  }
  record(session: SessionRow, ended = false): void {
    if (!activityEnabled(this.env)) return;
    try {
      this.ensure();
      const previous = this.ctx.storage.sql
        .exec<Pending>("SELECT * FROM public_activity_outbox_v2 WHERE id = 1")
        .toArray()[0];
      // Invite-only hosts never create an activity lease. A former public host
      // may send a zero-count tombstone to withdraw its earlier public lease.
      // A withdrawal is sent once. Later invite heartbeats must not renew its
      // retention or reveal private timing; existing bounded retries still run.
      if (session.visibility !== "public" && (!previous || previous.expires_at === 0))
        return;
      const version = Math.max(clock.now(), (previous?.version ?? 0) + 1);
      const expires =
        ended || session.visibility !== "public" || monitorIds(this.env).includes(session.serverId)
          ? 0
          : session.expiresAt;
      this.ctx.storage.sql.exec(
        "INSERT OR REPLACE INTO public_activity_outbox_v2 VALUES (1,?,?,?,?,0,?,?)",
        session.serverId,
        version,
        expires,
        expires === 0 ? 0 : session.players,
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
      .exec<Pending>("SELECT * FROM public_activity_outbox_v2 WHERE id = 1")
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
      "UPDATE public_activity_outbox_v2 SET attempts = attempts + 1, retry_at = ? WHERE version = ?",
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
          scope: "public-v2",
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
        "UPDATE public_activity_outbox_v2 SET attempts = 3 WHERE version = ?",
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
        "DELETE FROM public_activity_outbox_v2 WHERE keep_until <= ?",
        nowSeconds(),
      );
      const row = this.ctx.storage.sql
        .exec<Pending>("SELECT * FROM public_activity_outbox_v2 WHERE id = 1")
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
