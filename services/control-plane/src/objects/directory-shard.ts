/**
 * DirectoryShard: public listings for one game build and protocol major, split 16 ways by the first hexadecimal
 * digit of the server id. Because each shard holds one contiguous range of ids, a page across all 16 shards is the
 * concatenation of each shard's page in shard order.
 *
 * A listing carries its expiry, and reads ignore expired rows, so a host that stops heartbeating disappears after the
 * listing TTL even if its session's delisting call never arrives. An alarm deletes expired rows.
 */
import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { clock, nowSeconds } from "../shared/runtime";

export const DIRECTORY_SHARDS = 16;
export const MAXIMUM_LISTINGS_PER_SHARD = 4096;

export interface ListingPage {
  readonly listings: readonly string[];
  readonly more: boolean;
}

/** The shard name for a server, or for one shard index. */
export function directoryShardName(gameBuild: string, protocolMajor: string, serverIdOrIndex: string | number): string {
  const index = typeof serverIdOrIndex === "number" ? serverIdOrIndex : parseInt(serverIdOrIndex[0]!, 16);
  return `directory/${gameBuild}/${protocolMajor}/${index}`;
}

export class DirectoryShard extends DurableObject<Env> {
  private readonly sql: SqlStorage;
  private alarmAt: number | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec("CREATE TABLE IF NOT EXISTS listing (server_id TEXT PRIMARY KEY, body TEXT NOT NULL, expires_at INTEGER NOT NULL)");
    this.sql.exec("CREATE INDEX IF NOT EXISTS listing_expiry ON listing (expires_at)");
  }

  /** Adds or refreshes a listing. Refuses a new one when the shard is full. */
  async upsert(serverId: string, body: string, expiresAt: number): Promise<boolean> {
    this.purge();
    const exists = this.sql.exec("SELECT 1 FROM listing WHERE server_id = ?", serverId).toArray().length > 0;
    if (!exists) {
      const count = this.sql.exec<{ total: number }>("SELECT COUNT(*) AS total FROM listing").one().total;
      if (count >= MAXIMUM_LISTINGS_PER_SHARD) return false;
    }
    this.sql.exec(
      "INSERT INTO listing (server_id, body, expires_at) VALUES (?, ?, ?) ON CONFLICT (server_id) DO UPDATE SET body = excluded.body, expires_at = excluded.expires_at",
      serverId,
      body,
      expiresAt,
    );
    await this.scheduleCleanup();
    return true;
  }

  remove(serverId: string): void {
    this.sql.exec("DELETE FROM listing WHERE server_id = ?", serverId);
  }

  /** Up to `limit` live listings with ids after `after`, in id order. */
  page(after: string | null, limit: number): ListingPage {
    const rows = this.sql
      .exec<{ body: string }>(
        "SELECT body FROM listing WHERE server_id > ? AND expires_at > ? ORDER BY server_id LIMIT ?",
        after ?? "",
        nowSeconds(),
        limit + 1,
      )
      .toArray();
    return { listings: rows.slice(0, limit).map((row) => row.body), more: rows.length > limit };
  }

  override async alarm(): Promise<void> {
    this.alarmAt = null;
    this.purge();
    await this.scheduleCleanup();
  }

  private purge(): void {
    this.sql.exec("DELETE FROM listing WHERE expires_at <= ?", nowSeconds());
  }

  private async scheduleCleanup(): Promise<void> {
    const next = this.sql.exec<{ next: number | null }>("SELECT MIN(expires_at) AS next FROM listing").one().next;
    if (next === null) return;
    const at = Math.max(next * 1000, clock.now());
    if (this.alarmAt !== null && this.alarmAt <= at) return;
    this.alarmAt = at;
    await this.ctx.storage.setAlarm(at);
  }
}
