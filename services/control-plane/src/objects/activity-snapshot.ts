import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { ACTIVITY_SCHEMA, ACTIVITY_SHARDS, activityEnabled } from "../activity";
import { nowSeconds } from "../shared/runtime";

export interface ActivitySnapshotValue {
  readonly schema: typeof ACTIVITY_SCHEMA;
  readonly sampledAt: number;
  readonly state: "available" | "suppressed" | "unavailable";
  readonly scope: "public";
  readonly sessionsBand: { min: number; max: number } | null;
  readonly playersBand: { min: number; max: number } | null;
}
export class ActivitySnapshot extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec("DROP TABLE IF EXISTS snapshot");
    ctx.storage.sql.exec("DROP TABLE IF EXISTS warmup");
    ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS public_snapshot_v2 (sampled_at INTEGER PRIMARY KEY,collected_at INTEGER NOT NULL,state TEXT NOT NULL,sessions INTEGER,players INTEGER)",
    );
    ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS public_warmup_v2 (id INTEGER PRIMARY KEY,started_at INTEGER)",
    );
    ctx.storage.sql.exec("INSERT OR IGNORE INTO public_warmup_v2 VALUES (1,NULL)");
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
      }>("SELECT started_at FROM public_warmup_v2 WHERE id=1")
      .one().started_at;
    const enabled = activityEnabled(this.env);
    if (!enabled)
      this.ctx.storage.sql.exec("UPDATE public_warmup_v2 SET started_at=NULL WHERE id=1");
    else if (started === null) {
      started = now;
      this.ctx.storage.sql.exec(
        "UPDATE public_warmup_v2 SET started_at=? WHERE id=1",
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
        if (totals.some((row) => row.scope !== "public-v2"))
          throw new Error("unqualified activity totals");
        const s = totals.reduce((sum, row) => sum + row.sessions, 0),
          p = totals.reduce((sum, row) => sum + row.players, 0);
        state = s < 5 || p < 10 ? "suppressed" : "available";
        if (state === "available") {
          sessions = Math.floor(s / 5) * 5;
          players = Math.floor(p / 10) * 10;
        }
      } catch {
        /* A partial collection is unavailable; never publish a partial sum as zero. */
      }
    }
    // Suppression occurs before storage and publication, and each time slot is immutable.
    // Shard RPCs may stall: the privacy delay starts after the last result,
    // never from the earlier nominal sample time.
    const completedAt = nowSeconds();
    this.ctx.storage.sql.exec(
      "INSERT OR IGNORE INTO public_snapshot_v2 VALUES (?,?,?,?,?)",
      slot,
      completedAt,
      state,
      sessions,
      players,
    );
    this.ctx.storage.sql.exec(
      "DELETE FROM public_snapshot_v2 WHERE sampled_at < ?",
      completedAt - 86400,
    );
  }
  released(): ActivitySnapshotValue | null {
    const row = this.ctx.storage.sql
      .exec<{
        sampled_at: number;
        state: ActivitySnapshotValue["state"];
        sessions: number | null;
        players: number | null;
      }>(
        "SELECT * FROM public_snapshot_v2 WHERE collected_at <= ? AND collected_at >= ? ORDER BY sampled_at DESC LIMIT 1",
        nowSeconds() - 900,
        nowSeconds() - 86400,
      )
      .toArray()[0];
    return row
      ? {
          schema: ACTIVITY_SCHEMA,
          sampledAt: row.sampled_at,
          state: row.state,
          scope: "public",
          sessionsBand: row.sessions === null ? null : { min: row.sessions, max: row.sessions + 4 },
          playersBand: row.players === null ? null : { min: row.players, max: row.players + 9 },
        }
      : null;
  }
}
