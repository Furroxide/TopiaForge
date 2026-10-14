/**
 * The spend guard's durable state: feature flags and the kill switch, the latest usage measurement, per-ticket
 * relay accounting, the credentials minted and not yet forgotten, daily totals for the operator, and the hourly
 * per-prefix mint windows (windows.ts). Ticket keys are random ticket ids or token hashes, and credentials are the
 * provider's usernames: nothing here identifies a person or an address.
 */
import { ensureCounterTable, nextCounterExpiry, purgeCounters } from "./windows";

export interface Features {
  readonly hosting: boolean;
  readonly invites: boolean;
  readonly signaling: boolean;
  readonly relay: boolean;
}

export type KillReason = "manual" | "spend-cap" | "measurement-failed";

export interface KillSwitch {
  readonly engaged: boolean;
  readonly reason: KillReason | null;
  readonly since: number | null;
  /** The operator's note, shown only to admins. */
  readonly note: string | null;
}

export interface Measurement {
  /** The UTC day the latest successful measurement covered. */
  readonly day: string | null;
  readonly egressBytes: number;
  readonly measuredAt: number | null;
  readonly failures: number;
  readonly lastFailureAt: number | null;
}

export interface TicketAccount {
  readonly mints: number;
  readonly reservedBytes: number;
  readonly blocked: boolean;
}

export interface DayTotals {
  readonly day: string;
  readonly mints: number;
  readonly reservedBytes: number;
}

export const DEFAULT_FEATURES: Features = { hosting: true, invites: true, signaling: true, relay: true };
const RELEASED: KillSwitch = { engaged: false, reason: null, since: null, note: null };
const NEVER_MEASURED: Measurement = { day: null, egressBytes: 0, measuredAt: null, failures: 0, lastFailureAt: null };
/** Credentials are remembered this long past expiry, so the hourly measurement can still attribute their usage. */
export const CREDENTIAL_MEMORY_SECONDS = 2 * 60 * 60;
const DAY_MEMORY_SECONDS = 35 * 24 * 60 * 60;

export class SpendStore {
  constructor(private readonly sql: SqlStorage) {
    sql.exec("CREATE TABLE IF NOT EXISTS control (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
    sql.exec(
      "CREATE TABLE IF NOT EXISTS ticket (key TEXT PRIMARY KEY, mints INTEGER NOT NULL, reserved_bytes INTEGER NOT NULL, blocked INTEGER NOT NULL, forget_at INTEGER NOT NULL)",
    );
    sql.exec(
      `CREATE TABLE IF NOT EXISTS credential (username TEXT PRIMARY KEY, ticket TEXT NOT NULL, reserved_bytes INTEGER NOT NULL,
        expires_at INTEGER NOT NULL, revoke INTEGER NOT NULL, forget_at INTEGER NOT NULL)`,
    );
    sql.exec("CREATE INDEX IF NOT EXISTS credential_expiry ON credential (expires_at)");
    sql.exec("CREATE TABLE IF NOT EXISTS day (day TEXT PRIMARY KEY, mints INTEGER NOT NULL, reserved_bytes INTEGER NOT NULL, forget_at INTEGER NOT NULL)");
    ensureCounterTable(sql);
  }

  features(): Features {
    return { ...DEFAULT_FEATURES, ...this.read<Partial<Features>>("features", {}) };
  }

  setFeatures(features: Features): void {
    this.write("features", features);
  }

  killSwitch(): KillSwitch {
    return this.read("kill", RELEASED);
  }

  setKillSwitch(value: KillSwitch): void {
    this.write("kill", value);
  }

  measurement(): Measurement {
    return this.read("measurement", NEVER_MEASURED);
  }

  setMeasurement(value: Measurement): void {
    this.write("measurement", value);
  }

  ticket(key: string): TicketAccount {
    const row = this.sql
      .exec<{ mints: number; reserved_bytes: number; blocked: number }>("SELECT mints, reserved_bytes, blocked FROM ticket WHERE key = ?", key)
      .toArray()[0];
    return row === undefined ? { mints: 0, reservedBytes: 0, blocked: false } : { mints: row.mints, reservedBytes: row.reserved_bytes, blocked: row.blocked === 1 };
  }

  /** Adds (or, with negative numbers, refunds) mints and reserved bytes to a ticket's account. */
  chargeTicket(key: string, mints: number, bytes: number, forgetAt: number): void {
    this.sql.exec(
      `INSERT INTO ticket (key, mints, reserved_bytes, blocked, forget_at) VALUES (?, MAX(0, ?), MAX(0, ?), 0, ?)
       ON CONFLICT (key) DO UPDATE SET mints = MAX(0, mints + ?), reserved_bytes = MAX(0, reserved_bytes + ?), forget_at = MAX(forget_at, excluded.forget_at)`,
      key,
      mints,
      bytes,
      forgetAt,
      mints,
      bytes,
    );
  }

  blockTicket(key: string): void {
    this.sql.exec("UPDATE ticket SET blocked = 1 WHERE key = ?", key);
  }

  chargeDay(day: string, mints: number, bytes: number, now: number): void {
    this.sql.exec(
      `INSERT INTO day (day, mints, reserved_bytes, forget_at) VALUES (?, MAX(0, ?), MAX(0, ?), ?)
       ON CONFLICT (day) DO UPDATE SET mints = MAX(0, mints + ?), reserved_bytes = MAX(0, reserved_bytes + ?)`,
      day,
      mints,
      bytes,
      now + DAY_MEMORY_SECONDS,
      mints,
      bytes,
    );
  }

  dayTotals(day: string): DayTotals {
    const row = this.sql.exec<{ mints: number; reserved_bytes: number }>("SELECT mints, reserved_bytes FROM day WHERE day = ?", day).toArray()[0];
    return { day, mints: row?.mints ?? 0, reservedBytes: row?.reserved_bytes ?? 0 };
  }

  addCredential(username: string, ticket: string, reservedBytes: number, expiresAt: number): void {
    this.sql.exec(
      "INSERT INTO credential (username, ticket, reserved_bytes, expires_at, revoke, forget_at) VALUES (?, ?, ?, ?, 0, ?)",
      username,
      ticket,
      reservedBytes,
      expiresAt,
      expiresAt + CREDENTIAL_MEMORY_SECONDS,
    );
  }

  /** Gives a provisional credential row the provider's username once the mint succeeds. */
  confirmCredential(provisional: string, username: string, expiresAt: number): void {
    this.sql.exec(
      "UPDATE credential SET username = ?, expires_at = ?, forget_at = ? WHERE username = ?",
      username,
      expiresAt,
      expiresAt + CREDENTIAL_MEMORY_SECONDS,
      provisional,
    );
  }

  removeCredential(username: string): void {
    this.sql.exec("DELETE FROM credential WHERE username = ?", username);
  }

  credential(username: string): { readonly ticket: string; readonly reservedBytes: number } | null {
    const row = this.sql
      .exec<{ ticket: string; reserved_bytes: number }>("SELECT ticket, reserved_bytes FROM credential WHERE username = ?", username)
      .toArray()[0];
    return row === undefined ? null : { ticket: row.ticket, reservedBytes: row.reserved_bytes };
  }

  /** Reserved bytes of credentials that may still be relaying after `since`, provisional ones included. */
  outstandingBytes(since: number): number {
    return this.sql.exec<{ total: number | null }>("SELECT SUM(reserved_bytes) AS total FROM credential WHERE expires_at > ?", since).one().total ?? 0;
  }

  activeCredentials(now: number): number {
    return this.sql.exec<{ total: number }>("SELECT COUNT(*) AS total FROM credential WHERE expires_at > ? AND username NOT LIKE 'pending:%'", now).one().total;
  }

  /** Marks every live, confirmed credential for revocation. */
  revokeAllLive(now: number): void {
    this.sql.exec("UPDATE credential SET revoke = 1 WHERE expires_at > ? AND revoke = 0 AND username NOT LIKE 'pending:%'", now);
  }

  markForRevocation(username: string): void {
    this.sql.exec("UPDATE credential SET revoke = 1 WHERE username = ? AND revoke = 0", username);
  }

  pendingRevocations(now: number, limit: number): string[] {
    return this.sql
      .exec<{ username: string }>("SELECT username FROM credential WHERE revoke = 1 AND expires_at > ? ORDER BY expires_at LIMIT ?", now, limit)
      .toArray()
      .map((row) => row.username);
  }

  markRevoked(username: string): void {
    this.sql.exec("UPDATE credential SET revoke = 2 WHERE username = ?", username);
  }

  purge(now: number): void {
    purgeCounters(this.sql, now);
    this.sql.exec("DELETE FROM ticket WHERE forget_at <= ?", now);
    this.sql.exec("DELETE FROM credential WHERE forget_at <= ?", now);
    this.sql.exec("DELETE FROM day WHERE forget_at <= ?", now);
  }

  /** The earliest time a row falls due for deletion, in seconds. */
  nextDue(): number | null {
    const row = this.sql
      .exec<{ next: number | null }>(
        "SELECT MIN(t) AS next FROM (SELECT forget_at AS t FROM ticket UNION ALL SELECT forget_at FROM credential UNION ALL SELECT forget_at FROM day)",
      )
      .one();
    const counters = nextCounterExpiry(this.sql);
    const candidates = [row.next, counters].filter((value): value is number => value !== null);
    return candidates.length === 0 ? null : Math.min(...candidates);
  }

  private read<T>(key: string, fallback: T): T {
    const row = this.sql.exec<{ value: string }>("SELECT value FROM control WHERE key = ?", key).toArray()[0];
    return row === undefined ? fallback : (JSON.parse(row.value) as T);
  }

  private write(key: string, value: unknown): void {
    this.sql.exec(
      "INSERT INTO control (key, value) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
      key,
      JSON.stringify(value),
    );
  }
}
