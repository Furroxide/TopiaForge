/**
 * The operator's surface, behind ADMIN_TOKEN: the kill switch, feature flags, delisting a server, and the relay spend
 * report. Every authentication failure, including a service with no admin token configured, is the same 401.
 */
import type { JsonObject } from "../shared/canonical-json";
import { isDisplayText, sha256, utf8 } from "../shared/wire-text";
import type { Flags, SpendReport } from "../objects/spend-guard";
import type { RequestContext } from "./context";
import { ApiError, emptyResponse, jsonResponse, readJsonBody, requireNoBody } from "./http";
import { expectBoolean, expectInteger, expectObject } from "./validate";

const MINIMUM_ADMIN_TOKEN_LENGTH = 32;
const MAXIMUM_HOLD_SECONDS = 30 * 24 * 60 * 60;
export const ADMIN_LIMITS = [
  { window: "minute", limit: 30 },
  { window: "hour", limit: 300 },
] as const;

export async function requireAdmin(context: RequestContext): Promise<void> {
  await context.limit("admin", ADMIN_LIMITS);
  const configured = context.env.ADMIN_TOKEN;
  const presented = context.request.headers.get("authorization")?.startsWith("Bearer ") ? context.request.headers.get("authorization")!.slice(7) : null;
  if (typeof configured !== "string" || configured.length < MINIMUM_ADMIN_TOKEN_LENGTH || presented === null) throw new ApiError("unauthorized");
  const [expected, actual] = await Promise.all([sha256(utf8(configured)), sha256(utf8(presented))]);
  if (!crypto.subtle.timingSafeEqual(expected, actual)) throw new ApiError("unauthorized");
}

export async function adminState(context: RequestContext): Promise<Response> {
  requireNoBody(context.request);
  await requireAdmin(context);
  const guard = context.spendGuard();
  const [flags, spend] = await Promise.all([guard.flags(), guard.report()]);
  return jsonResponse(200, stateJson(context, flags, spend));
}

export async function setKillSwitch(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, 1024), ["engaged", "note"]);
  await requireAdmin(context);
  const engaged = expectBoolean(body["engaged"]);
  const note = body["note"];
  if (note !== null && !isDisplayText(note, 200)) throw new ApiError("invalid-request");
  const guard = context.spendGuard();
  const flags = await guard.setKillSwitch(engaged, note);
  context.rememberFlags(flags);
  return jsonResponse(200, stateJson(context, flags, await guard.report()));
}

export async function setFeatures(context: RequestContext): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, 1024), [], ["hosting", "invites", "signaling", "relay"]);
  await requireAdmin(context);
  const changes: Record<string, boolean> = {};
  for (const [key, value] of Object.entries(body)) changes[key] = expectBoolean(value);
  if (Object.keys(changes).length === 0) throw new ApiError("invalid-request");
  const guard = context.spendGuard();
  const flags = await guard.setFeatures(changes);
  context.rememberFlags(flags);
  return jsonResponse(200, stateJson(context, flags, await guard.report()));
}

export async function delist(context: RequestContext, serverId: string): Promise<Response> {
  const body = expectObject(await readJsonBody(context.request, 1024), ["holdSeconds"]);
  await requireAdmin(context);
  const holdSeconds = expectInteger(body["holdSeconds"], 0, MAXIMUM_HOLD_SECONDS);
  await context.hostSession(serverId).adminDelist(serverId, holdSeconds);
  return emptyResponse();
}

export async function measureSpend(context: RequestContext): Promise<Response> {
  requireNoBody(context.request);
  await requireAdmin(context);
  const guard = context.spendGuard();
  const spend = await guard.measure();
  return jsonResponse(200, stateJson(context, await guard.flags(), spend));
}

function stateJson(context: RequestContext, flags: Flags, spend: SpendReport): JsonObject {
  return {
    environment: context.config.environment,
    features: { ...flags.features },
    killSwitch: { ...flags.killSwitch },
    spend: {
      day: spend.day,
      mints: spend.mints,
      reservedBytes: spend.reservedBytes,
      outstandingBytes: spend.outstandingBytes,
      measuredBytes: spend.measurement.day === spend.day ? spend.measurement.egressBytes : 0,
      measuredAt: spend.measurement.measuredAt,
      measurementFailures: spend.measurement.failures,
      lastMeasurementFailureAt: spend.measurement.lastFailureAt,
      activeCredentials: spend.activeCredentials,
      pendingRevocations: spend.pendingRevocations,
      dailyCapBytes: spend.dailyCapBytes,
      ticketCapBytes: spend.ticketCapBytes,
      providerConfigured: spend.configured.provider,
      analyticsConfigured: spend.configured.analytics,
    },
    limits: {
      credentialTtlSeconds: context.config.limits.credentialTtlSeconds,
      ipMintsPerMinute: context.config.limits.ipMintsPerMinute,
      ipMintsPerHour: context.config.limits.ipMintsPerHour,
      ticketMaxMints: context.config.limits.ticketMaxMints,
      measurementFailureLimit: context.config.limits.measurementFailureLimit,
    },
  };
}
