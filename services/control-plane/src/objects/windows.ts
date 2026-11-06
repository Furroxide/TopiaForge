/**
 * Sliding-window counters in a Durable Object's SQLite storage, keyed by an hourly rate-limit key (keys.ts).
 *
 * A window's estimate is its own count plus the previous window's count weighted by how much of the previous window
 * still overlaps the last full window. Rows carry their expiry: a window's row lives until the end of the window after
 * it, so an hour's row is kept at most two hours and a minute's row at most two minutes. The caller passes the time
 * its keys were derived for, so the window arithmetic and the keys always agree.
 */

export type WindowKind = "minute" | "hour";

export interface WindowRule {
  readonly window: WindowKind;
  readonly limit: number;
}

/** The rate-limit keys for one address prefix: this hour's and the previous hour's. */
export interface BucketKeys {
  readonly hour: number;
  readonly current: string;
  readonly previous: string;
}

export interface WindowVerdict {
  readonly allowed: boolean;
  readonly retryAfterSeconds: number;
}

const WINDOW_SECONDS: Readonly<Record<WindowKind, number>> = { minute: 60, hour: 3600 };

export function ensureCounterTable(sql: SqlStorage): void {
  sql.exec("CREATE TABLE IF NOT EXISTS counter (key TEXT PRIMARY KEY, count INTEGER NOT NULL, expires_at INTEGER NOT NULL)");
  sql.exec("CREATE INDEX IF NOT EXISTS counter_expiry ON counter (expires_at)");
}

export function purgeCounters(sql: SqlStorage, nowSeconds: number): void {
  sql.exec("DELETE FROM counter WHERE expires_at <= ?", nowSeconds);
}

/** The earliest counter expiry, in seconds, or null when there are none. */
export function nextCounterExpiry(sql: SqlStorage): number | null {
  const row = sql.exec<{ next: number | null }>("SELECT MIN(expires_at) AS next FROM counter").one();
  return row.next;
}

/** Whether `cost` more events fit under every rule at `atMilliseconds`. Reads only. */
export function evaluate(
  sql: SqlStorage,
  scope: string,
  keys: BucketKeys,
  rules: readonly WindowRule[],
  cost: number,
  atMilliseconds: number,
): WindowVerdict {
  let allowed = true;
  let retryAfter = 0;
  for (const rule of rules) {
    const size = WINDOW_SECONDS[rule.window];
    const at = atMilliseconds / 1000;
    const index = Math.floor(at / size);
    const elapsed = (at - index * size) / size;
    const current = read(sql, rowKey(scope, keys, rule.window, index));
    const previous = read(sql, rowKey(scope, keys, rule.window, index - 1));
    if (current + previous * (1 - elapsed) + cost <= rule.limit) continue;
    allowed = false;
    let wait: number;
    if (current + cost > rule.limit || previous === 0) {
      wait = (index + 1) * size - at;
    } else {
      const needed = 1 - (rule.limit - cost - current) / previous;
      wait = Math.max(0, needed - elapsed) * size;
    }
    retryAfter = Math.max(retryAfter, Math.max(1, Math.ceil(wait)));
  }
  return { allowed, retryAfterSeconds: allowed ? 0 : retryAfter };
}

/** Adds `cost` events to the current window of every rule. */
export function increment(
  sql: SqlStorage,
  scope: string,
  keys: BucketKeys,
  rules: readonly WindowRule[],
  cost: number,
  atMilliseconds: number,
): void {
  for (const rule of rules) {
    const size = WINDOW_SECONDS[rule.window];
    const index = Math.floor(atMilliseconds / 1000 / size);
    const key = rowKey(scope, keys, rule.window, index);
    if (key === null) continue;
    sql.exec(
      "INSERT INTO counter (key, count, expires_at) VALUES (?, MAX(0, ?), ?) ON CONFLICT (key) DO UPDATE SET count = MAX(0, count + ?)",
      key,
      cost,
      (index + 2) * size,
      cost,
    );
  }
}

function read(sql: SqlStorage, key: string | null): number {
  if (key === null) return 0;
  const rows = sql.exec<{ count: number }>("SELECT count FROM counter WHERE key = ?", key).toArray();
  return rows[0]?.count ?? 0;
}

/** The row for window `index`, under the hourly key of the hour the window falls in; null outside the two known hours. */
function rowKey(scope: string, keys: BucketKeys, kind: WindowKind, index: number): string | null {
  const hour = kind === "hour" ? index : Math.floor(index / 60);
  const key = hour === keys.hour ? keys.current : hour === keys.hour - 1 ? keys.previous : null;
  return key === null ? null : `${scope}|${key}|${kind === "hour" ? "h" : "m"}|${index}`;
}
