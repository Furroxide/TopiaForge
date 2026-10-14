/**
 * HostSession: one per server id. It holds the session a host registered with a proof of its signing key: the signed
 * descriptor, a hash of the session token, the invites it issued, and the signaling room for joining peers.
 *
 * Heartbeats keep the session for LISTING_TTL_SECONDS. When they stop, the alarm delists the server, revokes its
 * invites, closes the room and deletes the session's storage, so nothing about an ended session remains.
 * Operations that change state run one at a time; calls to directory and invite shards are idempotent.
 */
import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import type { ErrorCode } from "../api/http";
import { stringify } from "../shared/canonical-json";
import { protocolMajor, type Visibility } from "../shared/descriptor";
import { inviteCodeFrom } from "../shared/invite-code";
import { hmacHex, serviceKeys } from "../shared/keys";
import { listingJson, type DescriptorSummary } from "../shared/listing";
import { PEER_ID_PATTERN } from "../shared/signaling";
import { clock, entropy, nowSeconds } from "../shared/runtime";
import { bytesEqual, utf8 } from "../shared/wire-text";
import { directoryShardName, type DirectoryShard } from "./directory-shard";
import { HostStore, type InviteRow, type SessionRow } from "./host-store";
import { CLOSE, acceptHost, acceptPeer, closeRoom, expirePeers, onClosed, onFrame } from "./host-signaling";
import { inviteShardName, type InviteShard } from "./invite-shard";

export const HEARTBEAT_SECONDS = 30;
export const LISTING_TTL_SECONDS = 90;
export const MAXIMUM_INVITES = 8;
export const PRIMARY_INVITE_SECONDS = 7 * 24 * 60 * 60;
export const MAXIMUM_INVITE_SECONDS = 7 * 24 * 60 * 60;
export const MINIMUM_INVITE_SECONDS = 5 * 60;
const MINIMUM_HEARTBEAT_GAP_SECONDS = 5;

export type Outcome<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: ErrorCode; readonly retryAfter?: number };

export interface Registration {
  readonly serverId: string;
  readonly descriptor: string;
  readonly descriptorSha256: string;
  readonly issuedAt: number;
  readonly visibility: Exclude<Visibility, "lan">;
  readonly summary: DescriptorSummary;
  readonly tokenHash: string;
  readonly challengeNonce: string;
  readonly challengeExpiresAt: number;
  readonly relayOnly: boolean;
}

export interface DescriptorUpdate {
  readonly descriptor: string;
  readonly descriptorSha256: string;
  readonly issuedAt: number;
  readonly visibility: Exclude<Visibility, "lan">;
  readonly summary: DescriptorSummary;
}

export interface SessionStatus {
  readonly expiresAt: number;
  readonly listed: boolean;
}

export interface HostView extends SessionStatus {
  readonly visibility: "public" | "invite";
  readonly players: number;
  readonly relayOnly: boolean;
  readonly descriptorSha256: string;
  readonly invites: readonly InviteRow[];
}

export interface Description {
  readonly visibility: "public" | "invite";
  readonly listing: string;
  readonly descriptor: string;
  readonly descriptorSha256: string;
}

export interface RelayGrant {
  /** Peers this side may relay to: every other player for the host, the host for a joiner. */
  readonly peers: number;
}

const ok = <T>(value: T): Outcome<T> => ({ ok: true, value });
const fail = <T>(error: ErrorCode, retryAfter?: number): Outcome<T> =>
  retryAfter === undefined ? { ok: false, error } : { ok: false, error, retryAfter };

export class HostSession extends DurableObject<Env> {
  private readonly store: HostStore;
  private queue: Promise<unknown> = Promise.resolve();
  private frames: Promise<void> = Promise.resolve();

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.store = new HostStore(ctx.storage.sql);
  }

  register(input: Registration): Promise<Outcome<SessionStatus & { readonly invite: InviteRow }>> {
    return this.serial(async () => {
      const now = nowSeconds();
      this.store.purgeExpired(now);
      if (this.store.holdUntil(now) !== null) return fail("server-blocked");
      // A session whose heartbeats stopped is ended properly before a new one starts, so none of its invites carry over.
      const stale = this.store.session();
      if (stale !== null && stale.expiresAt <= now) await this.endSession(stale, "expired");
      if (!this.store.useChallenge(input.challengeNonce, input.challengeExpiresAt)) return fail("challenge-invalid");
      const previous = this.store.liveSession(now);
      // The first invite is placed before the session is saved, so a failure leaves no half-registered session.
      const kept = previous === null ? null : (this.store.invites(now).find((row) => row.primary) ?? null);
      const invite = kept ?? (await this.issueInvite(input.serverId, now, PRIMARY_INVITE_SECONDS, true));
      if (invite === null) return fail("service-unavailable");
      if (previous !== null) {
        for (const socket of this.ctx.getWebSockets("host")) socket.close(CLOSE.replaced, "replaced");
      }
      const session: SessionRow = {
        serverId: input.serverId,
        tokenHash: input.tokenHash,
        descriptor: input.descriptor,
        descriptorSha256: input.descriptorSha256,
        issuedAt: input.issuedAt,
        visibility: input.visibility,
        directory: previous?.directory ?? null,
        summary: input.summary,
        players: 0,
        relayOnly: input.relayOnly,
        createdAt: now,
        heartbeatAt: now,
        expiresAt: now + LISTING_TTL_SECONDS,
      };
      let listed: boolean;
      try {
        listed = await this.publish(session);
      } catch (error) {
        if (kept === null) {
          this.store.removeInvite(invite.code);
          await this.invites(invite.code).revoke(invite.code, input.serverId);
        }
        throw error;
      }
      await this.scheduleAlarm();
      return ok({ expiresAt: session.expiresAt, listed, invite });
    });
  }

  view(serverId: string, tokenHash: string): Promise<Outcome<HostView>> {
    return this.serial(async () => {
      const now = nowSeconds();
      const session = this.authorized(serverId, tokenHash, now);
      if (session === null) return fail("unauthorized");
      return ok({
        expiresAt: session.expiresAt,
        listed: session.directory !== null,
        visibility: session.visibility,
        players: session.players,
        relayOnly: session.relayOnly,
        descriptorSha256: session.descriptorSha256,
        invites: this.store.invites(now),
      });
    });
  }

  heartbeat(serverId: string, tokenHash: string, players: number): Promise<Outcome<SessionStatus>> {
    return this.serial(async () => {
      const now = nowSeconds();
      const session = this.authorized(serverId, tokenHash, now);
      if (session === null) return fail("unauthorized");
      if (players > session.summary.maxPlayers) return fail("invalid-request");
      if (now - session.heartbeatAt < MINIMUM_HEARTBEAT_GAP_SECONDS) return fail("rate-limited", MINIMUM_HEARTBEAT_GAP_SECONDS);
      const updated: SessionRow = { ...session, players, heartbeatAt: now, expiresAt: now + LISTING_TTL_SECONDS };
      const listed = await this.publish(updated);
      await this.scheduleAlarm();
      return ok({ expiresAt: updated.expiresAt, listed });
    });
  }

  updateDescriptor(serverId: string, tokenHash: string, update: DescriptorUpdate): Promise<Outcome<SessionStatus>> {
    return this.serial(async () => {
      const now = nowSeconds();
      const session = this.authorized(serverId, tokenHash, now);
      if (session === null) return fail("unauthorized");
      if (update.issuedAt < session.issuedAt) return fail("stale-descriptor");
      if (!this.store.spend("descriptor", 6, 60, now)) return fail("rate-limited", 60 - (now % 60));
      const updated: SessionRow = {
        ...session,
        descriptor: update.descriptor,
        descriptorSha256: update.descriptorSha256,
        issuedAt: update.issuedAt,
        visibility: update.visibility,
        summary: update.summary,
        players: Math.min(session.players, update.summary.maxPlayers),
      };
      const listed = await this.publish(updated);
      return ok({ expiresAt: updated.expiresAt, listed });
    });
  }

  end(serverId: string, tokenHash: string): Promise<Outcome<null>> {
    return this.serial(async () => {
      const session = this.authorized(serverId, tokenHash, nowSeconds());
      if (session === null) return fail("unauthorized");
      await this.endSession(session, "session ended");
      return ok(null);
    });
  }

  createInvite(serverId: string, tokenHash: string, seconds: number): Promise<Outcome<InviteRow>> {
    return this.serial(async () => {
      const now = nowSeconds();
      const session = this.authorized(serverId, tokenHash, now);
      if (session === null) return fail("unauthorized");
      if (this.store.invites(now).length >= MAXIMUM_INVITES) return fail("invite-limit");
      if (!this.store.spend("invite", 30, 3600, now)) return fail("rate-limited", 3600 - (now % 3600));
      const invite = await this.issueInvite(serverId, now, seconds, false);
      return invite === null ? fail("service-unavailable") : ok(invite);
    });
  }

  revokeInvite(serverId: string, tokenHash: string, code: string): Promise<Outcome<null>> {
    return this.serial(async () => {
      const session = this.authorized(serverId, tokenHash, nowSeconds());
      if (session === null) return fail("unauthorized");
      if (!this.store.removeInvite(code)) return fail("not-found");
      await this.invites(code).revoke(code, serverId);
      return ok(null);
    });
  }

  /** What a joining player may see of a live session, or null. */
  async describe(serverId: string): Promise<Description | null> {
    const session = this.store.liveSession(nowSeconds());
    if (session === null || session.serverId !== serverId) return null;
    return {
      visibility: session.visibility,
      listing: stringify(listingJson(session.summary, listingState(session))),
      descriptor: session.descriptor,
      descriptorSha256: session.descriptorSha256,
    };
  }

  /**
   * Whether a player may have a join ticket. An invite-only session requires one of its own live invite codes; a
   * public one requires nothing. Every refusal is the same not-found.
   */
  async admit(serverId: string, inviteCode: string | null): Promise<Outcome<{ readonly hostRelayOnly: boolean }>> {
    const now = nowSeconds();
    const session = this.store.liveSession(now);
    if (session === null || session.serverId !== serverId) return fail("not-found");
    if (session.visibility === "invite" && !this.store.invites(now).some((row) => row.code === inviteCode)) return fail("not-found");
    if (inviteCode !== null && session.visibility === "public" && !this.store.invites(now).some((row) => row.code === inviteCode)) {
      return fail("not-found");
    }
    return ok({ hostRelayOnly: session.relayOnly });
  }

  /** Whether a side of this live session may mint relay credentials, and for how many peers. */
  async relayGrant(serverId: string, tokenHash: string | null): Promise<Outcome<RelayGrant>> {
    const now = nowSeconds();
    if (tokenHash !== null) {
      const session = this.authorized(serverId, tokenHash, now);
      return session === null ? fail("unauthorized") : ok({ peers: session.summary.maxPlayers - 1 });
    }
    const session = this.store.liveSession(now);
    return session === null || session.serverId !== serverId ? fail("not-found") : ok({ peers: 1 });
  }

  /** An operator's delisting: ends the session at once and refuses registration until the hold ends. */
  adminDelist(serverId: string, holdSeconds: number): Promise<void> {
    return this.serial(async () => {
      const now = nowSeconds();
      if (holdSeconds > 0) this.store.setHold(now + holdSeconds);
      const session = this.store.session();
      if (session !== null && session.serverId === serverId) await this.endSession(session, "delisted");
      else await this.tidy();
    });
  }

  /** Signaling: the Worker has authenticated the request and forwards it here with the facts it established. */
  override async fetch(request: Request): Promise<Response> {
    const now = nowSeconds();
    const serverId = request.headers.get("x-signal-server") ?? "";
    const session = this.store.liveSession(now);
    if (session === null || session.serverId !== serverId) return signalRefusal(404);
    const self = request.headers.get("x-signal-self") || null;
    if (request.headers.get("x-signal-role") === "host") {
      const tokenHash = request.headers.get("x-signal-token-hash") ?? "";
      if (!bytesEqual(utf8(tokenHash), utf8(session.tokenHash))) return signalRefusal(401);
      return acceptHost(this.ctx, now, session.relayOnly, self);
    }
    const peer = request.headers.get("x-signal-peer") ?? "";
    const expiresAt = Number(request.headers.get("x-signal-ticket-expires"));
    if (!PEER_ID_PATTERN.test(peer) || !Number.isSafeInteger(expiresAt) || expiresAt <= now) return signalRefusal(401);
    const relayOnly = request.headers.get("x-signal-relay-only") === "1";
    const accepted = acceptPeer(this.ctx, now, peer, relayOnly, self, expiresAt, session.relayOnly);
    if (accepted === null) return signalRefusal(409);
    await this.scheduleAlarm();
    return accepted;
  }

  /**
   * Frames are handled one at a time, in arrival order: checking a relay candidate awaits a hash, and without the
   * queue a later frame could overtake an earlier one.
   */
  override webSocketMessage(socket: WebSocket, frame: string | ArrayBuffer): Promise<void> {
    return this.inOrder(async () => {
      const keys = await serviceKeys(this.env.SERVICE_KEY);
      // Without keys no fingerprint can match, so a relay-only side's relay candidates still face every other check.
      const fingerprint = (address: string): Promise<string> => (keys === null ? Promise.resolve("") : hmacHex(keys.self, address));
      await onFrame({ ctx: this.ctx, now: nowSeconds(), fingerprint }, socket, frame);
    });
  }

  override webSocketClose(socket: WebSocket): Promise<void> {
    return this.inOrder(async () => onClosed(this.ctx, socket));
  }

  override webSocketError(socket: WebSocket): Promise<void> {
    return this.inOrder(async () => onClosed(this.ctx, socket));
  }

  private inOrder(work: () => Promise<void>): Promise<void> {
    const run = this.frames.then(work, work);
    this.frames = run.catch(() => undefined);
    return run;
  }

  override async alarm(): Promise<void> {
    await this.serial(async () => {
      const now = nowSeconds();
      const session = this.store.session();
      if (session !== null && session.expiresAt <= now) await this.endSession(session, "expired");
      else await this.tidy();
    });
  }

  private authorized(serverId: string, tokenHash: string, now: number): SessionRow | null {
    const session = this.store.liveSession(now);
    if (session === null || session.serverId !== serverId) return null;
    return bytesEqual(utf8(tokenHash), utf8(session.tokenHash)) ? session : null;
  }

  /** Saves the session and puts its listing where it belongs: in its directory shard when public, nowhere otherwise. */
  private async publish(session: SessionRow): Promise<boolean> {
    const wanted = session.visibility === "public" ? directoryShardName(session.summary.gameBuild, protocolMajor(session.summary.protocolVersion), session.serverId) : null;
    if (session.directory !== null && session.directory !== wanted) await this.directory(session.directory).remove(session.serverId);
    let directory: string | null = null;
    if (wanted !== null) {
      const body = stringify(listingJson(session.summary, listingState(session)));
      directory = (await this.directory(wanted).upsert(session.serverId, body, session.expiresAt)) ? wanted : null;
    }
    this.store.saveSession({ ...session, directory });
    return directory !== null;
  }

  private async issueInvite(serverId: string, now: number, seconds: number, primary: boolean): Promise<InviteRow | null> {
    for (let attempt = 0; attempt < 5; attempt++) {
      const code = inviteCodeFrom(entropy.bytes(10));
      const invite = { code, expiresAt: now + seconds, primary };
      const placement = await this.invites(code).place(code, serverId, invite.expiresAt);
      if (placement === "stored") {
        this.store.addInvite(invite);
        return invite;
      }
      if (placement === "full") return null;
    }
    return null;
  }

  private async endSession(session: SessionRow, reason: string): Promise<void> {
    if (session.directory !== null) await this.directory(session.directory).remove(session.serverId);
    for (const code of this.store.allInviteCodes()) await this.invites(code).revoke(code, session.serverId);
    closeRoom(this.ctx, CLOSE.sessionEnded, reason);
    this.store.deleteSession();
    await this.tidy();
  }

  /** Deletes what has expired; wipes storage entirely once nothing is left, or schedules the next alarm. */
  private async tidy(): Promise<void> {
    const now = nowSeconds();
    this.store.purgeExpired(now);
    expirePeers(this.ctx, now);
    if (this.store.isEmpty() && this.ctx.getWebSockets().length === 0) {
      await this.ctx.storage.deleteAlarm();
      await this.ctx.storage.deleteAll();
      this.store.wiped();
      return;
    }
    await this.scheduleAlarm();
  }

  private async scheduleAlarm(): Promise<void> {
    const due = this.store.nextDue();
    const peers = expirePeers(this.ctx, nowSeconds());
    const next = [due, peers].filter((value): value is number => value !== null);
    if (next.length === 0) {
      await this.ctx.storage.deleteAlarm();
      return;
    }
    await this.ctx.storage.setAlarm(Math.max(Math.min(...next) * 1000, clock.now()));
  }

  private serial<T>(work: () => Promise<T>): Promise<T> {
    const run = this.queue.then(work, work);
    this.queue = run.catch(() => undefined);
    return run;
  }

  private directory(name: string): DurableObjectStub<DirectoryShard> {
    return this.env.DIRECTORY_SHARDS.get(this.env.DIRECTORY_SHARDS.idFromName(name));
  }

  private invites(code: string): DurableObjectStub<InviteShard> {
    return this.env.INVITE_SHARDS.get(this.env.INVITE_SHARDS.idFromName(inviteShardName(code)));
  }
}

function listingState(session: SessionRow) {
  return {
    serverId: session.serverId,
    descriptorSha256: session.descriptorSha256,
    visibility: session.visibility,
    players: session.players,
    relayOnly: session.relayOnly,
    updatedAt: session.heartbeatAt,
  };
}

function signalRefusal(status: 401 | 404 | 409): Response {
  const code = status === 401 ? "unauthorized" : status === 404 ? "not-found" : "room-full";
  return new Response(stringify({ error: { code } }), { status });
}
