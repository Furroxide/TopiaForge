import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { ACTIVITY_SHARDS, activityEnabled } from "../activity";
import { nowSeconds } from "../shared/runtime";

export interface ActivitySnapshotValue {
  readonly schema: "topiaforge.activity/1";
  readonly sampledAt: number;
  readonly state: "available" | "suppressed" | "unavailable";
  readonly sessions: number | null;
  readonly players: number | null;
}
export class ActivitySnapshot extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS snapshot (sampled_at INTEGER PRIMARY KEY,collected_at INTEGER NOT NULL,state TEXT NOT NULL,sessions INTEGER,players INTEGER)",
    );
    ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS warmup (id INTEGER PRIMARY KEY,started_at INTEGER)",
    );
    ctx.storage.sql.exec("INSERT OR IGNORE INTO warmup VALUES (1,NULL)");
  }
  async collect(): Promise<void> {
    const now = nowSeconds(),
      slot = Math.floor(now / 300) * 300;
    let state: ActivitySnapshotValue["state"] = "unavailable",
      sessions: number | null = null,
      players: number | null = null;
    let started = this.ctx.storage.sql
      .exec<{
        started_at: number | null;
      }>("SELECT started_at FROM warmup WHERE id=1")
      .one().started_at;
    const enabled = activityEnabled(this.env);
    if (!enabled)
      this.ctx.storage.sql.exec("UPDATE warmup SET started_at=NULL WHERE id=1");
    else if (started === null) {
      started = now;
      this.ctx.storage.sql.exec(
        "UPDATE warmup SET started_at=? WHERE id=1",
        now,
      );
    }
    if (enabled && started !== null && now - started >= 90) {
      try {
        const totals = await Promise.all(
          Array.from({ length: ACTIVITY_SHARDS }, (_, i) =>
            this.env.ACTIVITY_SHARDS.get(
              this.env.ACTIVITY_SHARDS.idFromName(`activity/${i}`),
            ).totals(now),
          ),
        );
        const s = totals.reduce((sum, row) => sum + row.sessions, 0),
          p = totals.reduce((sum, row) => sum + row.players, 0);
        state = s < 5 || p < 10 ? "suppressed" : "available";
        if (state === "available") {
          sessions = s;
          players = p;
        }
      } catch {
        /* A partial collection is unavailable; never publish a partial sum as zero. */
      }
    }
    // Suppression occurs before storage and publication, and each time slot is immutable.
    this.ctx.storage.sql.exec(
      "INSERT OR IGNORE INTO snapshot VALUES (?,?,?,?,?)",
      slot,
      now,
      state,
      sessions,
      players,
    );
    this.ctx.storage.sql.exec(
      "DELETE FROM snapshot WHERE sampled_at < ?",
      now - 86400,
    );
  }
  released(): ActivitySnapshotValue | null {
    const row = this.ctx.storage.sql
      .exec<{
        sampled_at: number;
        state: ActivitySnapshotValue["state"];
        sessions: number | null;
        players: number | null;
      }>("SELECT * FROM snapshot WHERE collected_at <= ? ORDER BY sampled_at DESC LIMIT 1", nowSeconds() - 900)
      .toArray()[0];
    return row
      ? {
          schema: "topiaforge.activity/1",
          sampledAt: row.sampled_at,
          state: row.state,
          sessions: row.sessions,
          players: row.players,
        }
      : null;
  }
}
