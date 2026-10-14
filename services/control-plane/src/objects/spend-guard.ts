/**
 * SpendGuard: the one place relay credentials are minted, and the home of the feature flags and the kill switch.
 * There is a single instance, so every counter here is authoritative: a Durable Object runs one request's synchronous
 * code at a time, and every check and reservation below happens before the first await.
 *
 * Each mint reserves the credential's planned relay volume (TTL x peers x the per-connection budget) against:
 * - the ticket: at most TURN_TICKET_MAX_MINTS credentials and TURN_TICKET_CAP_GB reserved;
 * - the caller's address prefix: TURN_IP_MINTS_PER_MINUTE and TURN_IP_MINTS_PER_HOUR (sliding windows);
 * - the day: today's measured egress plus every reservation not yet measured stays under TURN_DAILY_CAP_GB.
 * A failed mint refunds all of it. The hourly measurement reads real egress: at the daily cap it trips the kill switch,
 * after repeated failures to measure it trips it too, and a credential that relays well past its reservation is
 * revoked and its ticket blocked.
 *
 * Tripping the switch revokes every live credential, including one whose mint is still waiting on the provider. A mint
 * looks again once the provider answers. If the relay was switched off meanwhile, even if it was switched on again, it
 * revokes the new credential and answers relay-unavailable. It also reads the ticket again, which may have expired or
 * been blocked by a measurement meanwhile, and asks the host session again whether the side's admission still holds:
 * a joiner's ticket stops holding when its invite is withdrawn or expires, when its session ends, or, for a ticket
 * without an invite, when the session goes private; a host's token stops holding when its session ends or a new
 * registration replaces it. Each of these gets the same revocation, and the answer a request made now would get.
 * Revocation runs in passes small enough for one Worker invocation's subrequests; a pass that makes progress is
 * followed at once by the next, through the alarm.
 */
import { DurableObject } from "cloudflare:workers";
import { readConfig, type RelayProvider } from "../config";
import type { Env } from "../env";
import type { IceServer, MintedCredentials } from "../relay/turn-api";
import { generateIceServers, revokeCredential } from "../relay/turn-api";
import { readTurnUsage } from "../relay/usage-api";
import { clock, entropy, nowSeconds } from "../shared/runtime";
import { toHex } from "../shared/wire-text";
import type { GrantSide } from "./host-session";
import { SpendStore, type Features, type KillReason, type KillSwitch, type Measurement } from "./spend-store";
import { evaluate, increment, type BucketKeys, type WindowRule } from "./windows";

/** Analytics trail real usage; credentials that expired this long before a measurement count as measured. */
export const MEASUREMENT_LATENCY_SECONDS = 15 * 60;
/** Provider calls in one revocation pass, inside the smallest per-invocation subrequest limit Workers apply (50). */
export const REVOCATIONS_PER_PASS = 40;
/** Revocations in flight at once; a Worker invocation keeps at most six connections open. */
const CONCURRENT_REVOCATIONS = 6;
/** How long a pass that revoked nothing waits before the next try. */
export const REVOCATION_RETRY_SECONDS = 60;
const MAXIMUM_NOTE_LENGTH = 200;

export interface Flags {
  readonly features: Features;
  readonly killSwitch: KillSwitch;
}

/** Whose admission a credential rests on: the session's server and the side, as the host session's relay grant reads it. */
export interface MintScope {
  readonly serverId: string;
  readonly side: GrantSide;
}

export interface MintRequest {
  /** "join:<ticket id>" for a joiner, "host:<token hash prefix>" for a host. */
  readonly ticket: string;
  readonly ticketExpiresAt: number;
  readonly peers: number;
  readonly keys: BucketKeys;
  /** The time the keys were derived for, in milliseconds. */
  readonly at: number;
  /** Checked again once the provider answers, so a credential never outlives the admission it was minted under. */
  readonly scope: MintScope;
}

/** Why a mint is refused after the provider answered because the side's admission lapsed while it waited. */
type Lapse = "unauthorized" | "not-found" | "internal";

/** Why a mint is refused after the provider answered; its credential is then revoked unseen. */
type MintRefusal = "relay-unavailable" | "ticket-limit" | Lapse;

export type MintOutcome =
  | { readonly ok: true; readonly iceServers: readonly IceServer[]; readonly expiresAt: number }
  | {
      readonly ok: false;
      readonly error: "rate-limited" | "upstream-failed" | MintRefusal;
      readonly retryAfter?: number;
    };

export interface SpendReport {
  readonly day: string;
  readonly mints: number;
  readonly reservedBytes: number;
  readonly outstandingBytes: number;
  readonly measurement: Measurement;
  readonly activeCredentials: number;
  readonly pendingRevocations: number;
  readonly dailyCapBytes: number | null;
  readonly ticketCapBytes: number | null;
  readonly configured: { readonly provider: boolean; readonly analytics: boolean };
}

export function utcDay(seconds: number): string {
  return new Date(seconds * 1000).toISOString().slice(0, 10);
}

export class SpendGuard extends DurableObject<Env> {
  private readonly store: SpendStore;
  private alarmAt: number | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.store = new SpendStore(ctx.storage.sql);
  }

  flags(): Flags {
    return { features: this.store.features(), killSwitch: this.store.killSwitch() };
  }

  setFeatures(changes: Partial<Features>): Flags {
    // Every mint waiting on the provider when relay is turned off is refused, even if relay is on again when it answers.
    if (changes.relay === false) this.store.markMintsInFlight();
    this.store.setFeatures({ ...this.store.features(), ...changes });
    return this.flags();
  }

  async setKillSwitch(engaged: boolean, note: string | null): Promise<Flags> {
    if (!engaged) {
      this.store.setKillSwitch({ engaged: false, reason: null, since: null, note: null });
      return this.flags();
    }
    this.trip("manual", note);
    const provider = readConfig(this.env).provider;
    await this.scheduleAlarm(provider !== null && (await this.revokePass(provider)));
    return this.flags();
  }

  async mint(request: MintRequest): Promise<MintOutcome> {
    const config = readConfig(this.env);
    const provider = config.provider;
    const limits = config.limits;
    const now = Math.floor(request.at / 1000);
    this.store.purge(nowSeconds());
    const flags = this.flags();
    if (provider === null || !flags.features.relay || flags.killSwitch.engaged) return { ok: false, error: "relay-unavailable" };

    const account = this.store.ticket(request.ticket);
    const reservation = limits.credentialTtlSeconds * request.peers * limits.reserveBytesPerPeerSecond;
    if (account.blocked || request.ticketExpiresAt <= now || account.mints >= limits.ticketMaxMints) return { ok: false, error: "ticket-limit" };
    if (account.reservedBytes + reservation > provider.ticketCapBytes) return { ok: false, error: "ticket-limit" };
    const rules: WindowRule[] = [
      { window: "minute", limit: limits.ipMintsPerMinute },
      { window: "hour", limit: limits.ipMintsPerHour },
    ];
    const verdict = evaluate(this.ctx.storage.sql, "mint", request.keys, rules, 1, request.at);
    if (!verdict.allowed) return { ok: false, error: "rate-limited", retryAfter: verdict.retryAfterSeconds };
    if (this.committedBytes(now) + reservation > provider.dailyCapBytes) return { ok: false, error: "relay-unavailable" };

    // Reserve everything before the first await, so concurrent mints see each other.
    const provisional = `pending:${toHex(entropy.bytes(16))}`;
    const forgetAt = Math.max(request.ticketExpiresAt, now + limits.credentialTtlSeconds) + 24 * 60 * 60;
    increment(this.ctx.storage.sql, "mint", request.keys, rules, 1, request.at);
    this.store.chargeTicket(request.ticket, 1, reservation, forgetAt);
    this.store.chargeDay(utcDay(now), 1, reservation, now);
    this.store.addCredential(provisional, request.ticket, reservation, now + limits.credentialTtlSeconds);

    const refund = (): void => {
      increment(this.ctx.storage.sql, "mint", request.keys, rules, -1, request.at);
      this.store.chargeTicket(request.ticket, -1, -reservation, forgetAt);
      this.store.chargeDay(utcDay(now), -1, -reservation, now);
    };
    let minted: MintedCredentials;
    try {
      minted = await generateIceServers(provider, limits.credentialTtlSeconds);
    } catch {
      refund();
      this.store.removeCredential(provisional);
      return { ok: false, error: "upstream-failed" };
    }
    const expiresAt = nowSeconds() + limits.credentialTtlSeconds;
    // The side's admission may have lapsed while the provider answered, so the host session is asked again.
    const lapse = await this.admissionLapse(request.scope);
    // Nothing awaits between the checks in `refusalOnceMinted` and the credential's confirmation, and a refused
    // credential is never handed out.
    const refusal = this.refusalOnceMinted(request, provisional, lapse);
    if (refusal !== null) {
      refund();
      this.store.withdrawCredential(provisional, minted.username, expiresAt);
      if (await revokeCredential(provider, minted.username)) this.store.markRevoked(minted.username);
      await this.scheduleAlarm(false);
      return { ok: false, error: refusal };
    }
    this.store.confirmCredential(provisional, minted.username, expiresAt);
    await this.scheduleAlarm(false);
    return { ok: true, iceServers: minted.iceServers, expiresAt };
  }

  /** The hourly measurement, also run on demand by an admin. */
  async measure(): Promise<SpendReport> {
    const config = readConfig(this.env);
    const now = nowSeconds();
    this.store.purge(now);
    const provider = config.provider;
    let progressed = false;
    if (provider !== null) {
      const previous = this.store.measurement();
      try {
        if (config.analytics === null) throw new Error("analytics not configured");
        const day = utcDay(now);
        const usage = await readTurnUsage(config.analytics, provider.keyId, day);
        this.store.setMeasurement({ day, egressBytes: usage.egressBytes, measuredAt: now, failures: 0, lastFailureAt: previous.lastFailureAt });
        for (const [username, bytes] of usage.byUsername) {
          const credential = this.store.credential(username);
          if (credential !== null && bytes > credential.reservedBytes * config.limits.overuseFactor) {
            this.store.markForRevocation(username);
            this.store.blockTicket(credential.ticket);
          }
        }
        if (usage.egressBytes >= provider.dailyCapBytes) this.trip("spend-cap", null);
      } catch {
        const failures = previous.failures + 1;
        this.store.setMeasurement({ ...previous, failures, lastFailureAt: now });
        const limit = config.limits.measurementFailureLimit;
        if (limit > 0 && failures >= limit) this.trip("measurement-failed", null);
      }
      progressed = await this.revokePass(provider);
    }
    await this.scheduleAlarm(progressed);
    return this.report();
  }

  report(): SpendReport {
    const config = readConfig(this.env);
    const now = nowSeconds();
    const totals = this.store.dayTotals(utcDay(now));
    return {
      day: totals.day,
      mints: totals.mints,
      reservedBytes: totals.reservedBytes,
      outstandingBytes: this.committedBytes(now) - this.measuredToday(now),
      measurement: this.store.measurement(),
      activeCredentials: this.store.activeCredentials(now),
      pendingRevocations: this.store.pendingRevocations(now, 1000).length,
      dailyCapBytes: config.provider?.dailyCapBytes ?? null,
      ticketCapBytes: config.provider?.ticketCapBytes ?? null,
      configured: { provider: config.provider !== null, analytics: config.analytics !== null },
    };
  }

  override async alarm(): Promise<void> {
    this.alarmAt = null;
    this.store.purge(nowSeconds());
    const provider = readConfig(this.env).provider;
    await this.scheduleAlarm(provider !== null && (await this.revokePass(provider)));
  }

  /**
   * What a mint whose provider has answered is refused with, or null to hand its credential out. Every refusal that can
   * arise while the provider answers is read again here, synchronously, in the order a request made now would meet
   * them, and answered as that request would be:
   * - relay-unavailable when the kill switch or turning relay off marked the mint, or the switch or the flag reads so
   *   now, even if it read otherwise in between;
   * - unauthorized once the ticket has expired;
   * - the admission's lapse, which the host session was asked about once the provider answered;
   * - ticket-limit once a measurement has blocked the ticket for relaying past its plan.
   * The ticket's, the prefix's and the day's limits need no second look, because this mint's reservation already holds
   * its place under each of them.
   */
  private refusalOnceMinted(request: MintRequest, provisional: string, lapse: Lapse | null): MintRefusal | null {
    const flags = this.flags();
    if (this.store.isMarked(provisional) || !flags.features.relay || flags.killSwitch.engaged) return "relay-unavailable";
    if (request.ticketExpiresAt <= nowSeconds()) return "unauthorized";
    if (lapse !== null) return lapse;
    return this.store.ticket(request.ticket).blocked ? "ticket-limit" : null;
  }

  /**
   * Null while the side's admission still holds in its host session; otherwise what its request would be answered now:
   * unauthorized for a ticket that stopped holding or a host token that no longer opens the session, not-found for a
   * joiner whose session has ended. A host session that cannot be asked holds nothing.
   */
  private async admissionLapse(scope: MintScope): Promise<Lapse | null> {
    try {
      const session = this.env.HOST_SESSIONS.get(this.env.HOST_SESSIONS.idFromName(scope.serverId));
      const grant = await session.relayGrant(scope.serverId, scope.side);
      return grant.ok ? null : grant.error === "not-found" ? "not-found" : "unauthorized";
    } catch {
      return "internal";
    }
  }

  /** Today's measured egress plus every reservation the latest measurement cannot have seen yet. */
  private committedBytes(now: number): number {
    const measurement = this.store.measurement();
    const today = utcDay(now);
    const measuredToday = this.measuredToday(now);
    const startOfDay = Date.parse(`${today}T00:00:00Z`) / 1000;
    const seenUntil = measurement.day === today && measurement.measuredAt !== null ? measurement.measuredAt : startOfDay;
    return measuredToday + this.store.outstandingBytes(seenUntil - MEASUREMENT_LATENCY_SECONDS);
  }

  private measuredToday(now: number): number {
    const measurement = this.store.measurement();
    return measurement.day === utcDay(now) ? measurement.egressBytes : 0;
  }

  /** Engages the kill switch and marks every live credential for revocation, those still being minted included. */
  private trip(reason: KillReason, note: string | null): void {
    const now = nowSeconds();
    if (!this.store.killSwitch().engaged) {
      const trimmed = note === null ? null : note.slice(0, MAXIMUM_NOTE_LENGTH);
      this.store.setKillSwitch({ engaged: true, reason, since: now, note: trimmed });
    }
    this.store.revokeAllLive(now);
  }

  /**
   * One revocation pass: up to REVOCATIONS_PER_PASS marked credentials, CONCURRENT_REVOCATIONS at a time. Passes chain
   * through the alarm, which gives each its own invocation. True when the pass revoked anything.
   */
  private async revokePass(provider: RelayProvider): Promise<boolean> {
    const batch = this.store.pendingRevocations(nowSeconds(), REVOCATIONS_PER_PASS);
    let revoked = 0;
    for (let start = 0; start < batch.length; start += CONCURRENT_REVOCATIONS) {
      const group = batch.slice(start, start + CONCURRENT_REVOCATIONS);
      const results = await Promise.all(group.map((username) => revokeCredential(provider, username)));
      results.forEach((done, index) => {
        if (!done) return;
        this.store.markRevoked(group[index]!);
        revoked++;
      });
    }
    return revoked > 0;
  }

  /**
   * The next alarm: when a row falls due for deletion, or when revocations are still owed. After a pass that made
   * progress the next pass runs at once; after one that made none, REVOCATION_RETRY_SECONDS later.
   */
  private async scheduleAlarm(progressed: boolean): Promise<void> {
    const now = nowSeconds();
    const due = this.store.nextDue();
    const owed = this.store.pendingRevocations(now, 1).length > 0;
    const retry = owed ? (progressed ? now : now + REVOCATION_RETRY_SECONDS) : null;
    const next = [due, retry].filter((value): value is number => value !== null);
    if (next.length === 0) return;
    const at = Math.max(Math.min(...next) * 1000, clock.now());
    if (this.alarmAt !== null && this.alarmAt <= at) return;
    this.alarmAt = at;
    await this.ctx.storage.setAlarm(at);
  }
}
