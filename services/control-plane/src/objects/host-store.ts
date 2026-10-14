/**
 * The host session's durable state, in its own SQLite database: the session (the signed descriptor, a hash of the
 * session token, its random epoch, and what the listing shows), the invites it issued, the challenge nonces it
 * accepted, an operator's hold, small per-session operation budgets, and the peer ids the host said goodbye to. No
 * address is ever stored here, and signaling frames live only in memory.
 */
import type { DescriptorSummary } from "../shared/listing";

const MAXIMUM_EXCLUDED_PEERS = 1024;

export interface SessionRow {
  readonly serverId: string;
  readonly tokenHash: string;
  /** Random per session and kept across re-registration, so join tickets are honoured by the session they came from. */
  readonly epoch: string;
  readonly descriptor: string;
  readonly descriptorSha256: string;
  readonly issuedAt: number;
  readonly visibility: "public" | "invite";
  /** The directory shard holding this session's listing, or null when unlisted. */
  readonly directory: string | null;
  readonly summary: DescriptorSummary;
  readonly players: number;
  readonly relayOnly: boolean;
  readonly createdAt: number;
  readonly heartbeatAt: number;
  readonly expiresAt: number;
}

export interface InviteRow {
  readonly code: string;
  readonly expiresAt: number;
  readonly primary: boolean;
}

interface SessionRecord extends Record<string, SqlStorageValue> {
  server_id: string;
  token_hash: string;
  epoch: string;
  descriptor: string;
  descriptor_sha256: string;
  issued_at: number;
  visibility: string;
  directory: string | null;
  summary: string;
  players: number;
  relay_only: number;
  created_at: number;
  heartbeat_at: number;
  expires_at: number;
}

export class HostStore {
  private ready = false;

  constructor(private readonly sql: SqlStorage) {}

  /** Creates the tables. Called before every use, because ending a session deletes them. */
  ensure(): void {
    if (this.ready) return;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS session (
      id INTEGER PRIMARY KEY CHECK (id = 1),
      server_id TEXT NOT NULL, token_hash TEXT NOT NULL, epoch TEXT NOT NULL, descriptor TEXT NOT NULL, descriptor_sha256 TEXT NOT NULL,
      issued_at INTEGER NOT NULL, visibility TEXT NOT NULL, directory TEXT, summary TEXT NOT NULL,
      players INTEGER NOT NULL, relay_only INTEGER NOT NULL, created_at INTEGER NOT NULL,
      heartbeat_at INTEGER NOT NULL, expires_at INTEGER NOT NULL)`);
    this.sql.exec("CREATE TABLE IF NOT EXISTS invite (code TEXT PRIMARY KEY, expires_at INTEGER NOT NULL, is_primary INTEGER NOT NULL)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS used_challenge (nonce TEXT PRIMARY KEY, expires_at INTEGER NOT NULL)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS hold (id INTEGER PRIMARY KEY CHECK (id = 1), until INTEGER NOT NULL)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS budget (name TEXT PRIMARY KEY, window_start INTEGER NOT NULL, count INTEGER NOT NULL)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS excluded_peer (peer TEXT PRIMARY KEY, until INTEGER NOT NULL)");
    this.ready = true;
  }

  /** Forgets that the tables exist, after the database was wiped. */
  wiped(): void {
    this.ready = false;
  }

  session(): SessionRow | null {
    this.ensure();
    const rows = this.sql.exec<SessionRecord>("SELECT * FROM session WHERE id = 1").toArray();
    const row = rows[0];
    if (row === undefined) return null;
    return {
      serverId: row.server_id,
      tokenHash: row.token_hash,
      epoch: row.epoch,
      descriptor: row.descriptor,
      descriptorSha256: row.descriptor_sha256,
      issuedAt: row.issued_at,
      visibility: row.visibility === "invite" ? "invite" : "public",
      directory: row.directory,
      summary: JSON.parse(row.summary) as DescriptorSummary,
      players: row.players,
      relayOnly: row.relay_only === 1,
      createdAt: row.created_at,
      heartbeatAt: row.heartbeat_at,
      expiresAt: row.expires_at,
    };
  }

  /** The session while it is live at `now`, or null. */
  liveSession(now: number): SessionRow | null {
    const session = this.session();
    return session !== null && session.expiresAt > now ? session : null;
  }

  saveSession(row: SessionRow): void {
    this.ensure();
    this.sql.exec(
      `INSERT INTO session (id, server_id, token_hash, epoch, descriptor, descriptor_sha256, issued_at, visibility, directory, summary,
         players, relay_only, created_at, heartbeat_at, expires_at)
       VALUES (1, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT (id) DO UPDATE SET server_id = excluded.server_id, token_hash = excluded.token_hash, epoch = excluded.epoch,
         descriptor = excluded.descriptor, descriptor_sha256 = excluded.descriptor_sha256, issued_at = excluded.issued_at,
         visibility = excluded.visibility, directory = excluded.directory, summary = excluded.summary,
         players = excluded.players, relay_only = excluded.relay_only, created_at = excluded.created_at,
         heartbeat_at = excluded.heartbeat_at, expires_at = excluded.expires_at`,
      row.serverId,
      row.tokenHash,
      row.epoch,
      row.descriptor,
      row.descriptorSha256,
      row.issuedAt,
      row.visibility,
      row.directory,
      JSON.stringify(row.summary),
      row.players,
      row.relayOnly ? 1 : 0,
      row.createdAt,
      row.heartbeatAt,
      row.expiresAt,
    );
  }

  deleteSession(): void {
    this.ensure();
    this.sql.exec("DELETE FROM session");
    this.sql.exec("DELETE FROM invite");
    this.sql.exec("DELETE FROM budget");
    this.sql.exec("DELETE FROM excluded_peer");
  }

  invites(now: number): InviteRow[] {
    this.ensure();
    return this.sql
      .exec<{ code: string; expires_at: number; is_primary: number }>(
        "SELECT code, expires_at, is_primary FROM invite WHERE expires_at > ? ORDER BY expires_at, code",
        now,
      )
      .toArray()
      .map((row) => ({ code: row.code, expiresAt: row.expires_at, primary: row.is_primary === 1 }));
  }

  allInviteCodes(): string[] {
    this.ensure();
    return this.sql.exec<{ code: string }>("SELECT code FROM invite").toArray().map((row) => row.code);
  }

  addInvite(invite: InviteRow): void {
    this.ensure();
    this.sql.exec("INSERT INTO invite (code, expires_at, is_primary) VALUES (?, ?, ?)", invite.code, invite.expiresAt, invite.primary ? 1 : 0);
  }

  removeInvite(code: string): boolean {
    this.ensure();
    return this.sql.exec("DELETE FROM invite WHERE code = ? RETURNING code", code).toArray().length > 0;
  }

  purgeExpired(now: number): void {
    this.ensure();
    this.sql.exec("DELETE FROM invite WHERE expires_at <= ?", now);
    this.sql.exec("DELETE FROM used_challenge WHERE expires_at <= ?", now);
    this.sql.exec("DELETE FROM hold WHERE until <= ?", now);
    this.sql.exec("DELETE FROM excluded_peer WHERE until <= ?", now);
  }

  /**
   * Keeps a peer id the host said goodbye to out of the room until `until`, when its ticket expires and can open
   * nothing more. The list is capped: when it is full, the entry that would expire first makes way.
   */
  excludePeer(peer: string, until: number): void {
    this.ensure();
    const count = this.sql.exec<{ total: number }>("SELECT COUNT(*) AS total FROM excluded_peer").one().total;
    if (count >= MAXIMUM_EXCLUDED_PEERS) {
      this.sql.exec("DELETE FROM excluded_peer WHERE peer = (SELECT peer FROM excluded_peer ORDER BY until LIMIT 1)");
    }
    this.sql.exec("INSERT INTO excluded_peer (peer, until) VALUES (?, ?) ON CONFLICT (peer) DO UPDATE SET until = MAX(until, excluded.until)", peer, until);
  }

  isExcluded(peer: string, now: number): boolean {
    this.ensure();
    return this.sql.exec("SELECT 1 FROM excluded_peer WHERE peer = ? AND until > ?", peer, now).toArray().length > 0;
  }

  /** Records a challenge nonce; false when it was already used. */
  useChallenge(nonce: string, expiresAt: number): boolean {
    this.ensure();
    const rows = this.sql.exec("INSERT INTO used_challenge (nonce, expires_at) VALUES (?, ?) ON CONFLICT (nonce) DO NOTHING RETURNING nonce", nonce, expiresAt).toArray();
    return rows.length > 0;
  }

  holdUntil(now: number): number | null {
    this.ensure();
    const rows = this.sql.exec<{ until: number }>("SELECT until FROM hold WHERE id = 1 AND until > ?", now).toArray();
    return rows[0]?.until ?? null;
  }

  setHold(until: number): void {
    this.ensure();
    this.sql.exec("INSERT INTO hold (id, until) VALUES (1, ?) ON CONFLICT (id) DO UPDATE SET until = MAX(until, excluded.until)", until);
  }

  /** Counts one use of a per-session budget in a fixed window; false when the window is already spent. */
  spend(name: string, limit: number, windowSeconds: number, now: number): boolean {
    this.ensure();
    const start = now - (now % windowSeconds);
    const rows = this.sql.exec<{ window_start: number; count: number }>("SELECT window_start, count FROM budget WHERE name = ?", name).toArray();
    const row = rows[0];
    const count = row !== undefined && row.window_start === start ? row.count : 0;
    if (count >= limit) return false;
    this.sql.exec(
      "INSERT INTO budget (name, window_start, count) VALUES (?, ?, ?) ON CONFLICT (name) DO UPDATE SET window_start = excluded.window_start, count = excluded.count",
      name,
      start,
      count + 1,
    );
    return true;
  }

  /**
   * The earliest time anything stored here falls due: session expiry, an invite's expiry (its tickets' sockets close
   * then), a used challenge's expiry, or a hold's end.
   */
  nextDue(): number | null {
    this.ensure();
    const row = this.sql
      .exec<{ next: number | null }>(
        `SELECT MIN(t) AS next FROM (
           SELECT expires_at AS t FROM session UNION ALL SELECT expires_at FROM invite UNION ALL SELECT expires_at FROM used_challenge
           UNION ALL SELECT until FROM hold)`,
      )
      .one();
    return row.next;
  }

  /** Whether anything durable remains. */
  isEmpty(): boolean {
    this.ensure();
    const row = this.sql
      .exec<{ total: number }>(
        `SELECT (SELECT COUNT(*) FROM session) + (SELECT COUNT(*) FROM used_challenge) + (SELECT COUNT(*) FROM hold)
           + (SELECT COUNT(*) FROM invite) + (SELECT COUNT(*) FROM excluded_peer) AS total`,
      )
      .one();
    return row.total === 0;
  }
}
