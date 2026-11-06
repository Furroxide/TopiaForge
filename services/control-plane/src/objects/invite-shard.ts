/**
 * InviteShard: invite code lookups, split 32 ways by the code's first character. A code maps to the server it
 * invites to until it expires or its host revokes it; the host session also revokes every code it issued when it
 * ends. Reads ignore expired rows and an alarm deletes them.
 */
import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { clock, nowSeconds } from "../shared/runtime";

export const MAXIMUM_INVITES_PER_SHARD = 65536;

export type InvitePlacement = "stored" | "taken" | "full";

export function inviteShardName(code: string): string {
  return `invites/${code[0]!}`;
}

export class InviteShard extends DurableObject<Env> {
  private readonly sql: SqlStorage;
  private alarmAt: number | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec("CREATE TABLE IF NOT EXISTS invite (code TEXT PRIMARY KEY, server_id TEXT NOT NULL, expires_at INTEGER NOT NULL)");
    this.sql.exec("CREATE INDEX IF NOT EXISTS invite_expiry ON invite (expires_at)");
  }

  async place(code: string, serverId: string, expiresAt: number): Promise<InvitePlacement> {
    this.purge();
    if (this.sql.exec("SELECT 1 FROM invite WHERE code = ?", code).toArray().length > 0) return "taken";
    const count = this.sql.exec<{ total: number }>("SELECT COUNT(*) AS total FROM invite").one().total;
    if (count >= MAXIMUM_INVITES_PER_SHARD) return "full";
    this.sql.exec("INSERT INTO invite (code, server_id, expires_at) VALUES (?, ?, ?)", code, serverId, expiresAt);
    await this.scheduleCleanup();
    return "stored";
  }

  /** Removes a code, but only on behalf of the server it belongs to. */
  revoke(code: string, serverId: string): boolean {
    const rows = this.sql.exec("DELETE FROM invite WHERE code = ? AND server_id = ? RETURNING code", code, serverId).toArray();
    return rows.length > 0;
  }

  /** The server a live code invites to, or null. */
  resolve(code: string): string | null {
    const rows = this.sql
      .exec<{ server_id: string }>("SELECT server_id FROM invite WHERE code = ? AND expires_at > ?", code, nowSeconds())
      .toArray();
    return rows[0]?.server_id ?? null;
  }

  override async alarm(): Promise<void> {
    this.alarmAt = null;
    this.purge();
    await this.scheduleCleanup();
  }

  private purge(): void {
    this.sql.exec("DELETE FROM invite WHERE expires_at <= ?", nowSeconds());
  }

  private async scheduleCleanup(): Promise<void> {
    const next = this.sql.exec<{ next: number | null }>("SELECT MIN(expires_at) AS next FROM invite").one().next;
    if (next === null) return;
    const at = Math.max(next * 1000, clock.now());
    if (this.alarmAt !== null && this.alarmAt <= at) return;
    this.alarmAt = at;
    await this.ctx.storage.setAlarm(at);
  }
}
