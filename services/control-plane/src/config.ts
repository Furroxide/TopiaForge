/**
 * Parses the Worker's vars and secrets into bounded settings. A value that is missing or out of range falls back to
 * its default when one is safe, and otherwise leaves its feature unavailable: relay minting never runs on a guess.
 */
import type { Env } from "./env";

export interface RelayLimits {
  /** Lifetime of each minted TURN credential, which is also the kill switch's worst-case latency. */
  readonly credentialTtlSeconds: number;
  readonly ipMintsPerHour: number;
  readonly ipMintsPerMinute: number;
  readonly ticketMaxMints: number;
  /** Bytes a credential may plan to relay per peer per second: the 256 kbit/s per-connection budget. */
  readonly reserveBytesPerPeerSecond: number;
  /** Consecutive failed hourly measurements that trip the kill switch; 0 never trips on failure. */
  readonly measurementFailureLimit: number;
  /** How far past its reservation a credential's measured egress may go before it is revoked. */
  readonly overuseFactor: number;
}

export interface RelayProvider {
  readonly keyId: string;
  readonly apiToken: string;
  readonly dailyCapBytes: number;
  readonly ticketCapBytes: number;
}

export interface Analytics {
  readonly accountId: string;
  readonly apiToken: string;
}

export interface Config {
  readonly environment: string;
  readonly limits: RelayLimits;
  /** Null until the TURN key and both spend ceilings are configured. */
  readonly provider: RelayProvider | null;
  /** Null until the analytics account and token are configured. */
  readonly analytics: Analytics | null;
  readonly flagCacheMilliseconds: number;
}

const GIGABYTE = 1_000_000_000;
const TOKEN_PATTERN = /^[A-Za-z0-9._~+/=-]{8,512}$/;
const KEY_ID_PATTERN = /^[A-Za-z0-9_-]{8,128}$/;
const ACCOUNT_PATTERN = /^[0-9a-f]{32}$/;

export function readConfig(env: Env): Config {
  const limits: RelayLimits = {
    credentialTtlSeconds: integer(env.TURN_CREDENTIAL_TTL_SECONDS, 600, 60, 3600),
    ipMintsPerHour: integer(env.TURN_IP_MINTS_PER_HOUR, 20, 1, 1000),
    ipMintsPerMinute: integer(env.TURN_IP_MINTS_PER_MINUTE, 5, 1, 100),
    ticketMaxMints: integer(env.TURN_TICKET_MAX_MINTS, 36, 1, 1000),
    reserveBytesPerPeerSecond: 32 * 1024,
    measurementFailureLimit: integer(env.TURN_MEASUREMENT_FAILURE_LIMIT, 3, 0, 48),
    overuseFactor: 2,
  };
  const dailyCapBytes = gigabytes(env.TURN_DAILY_CAP_GB);
  const ticketCapBytes = gigabytes(env.TURN_TICKET_CAP_GB);
  const provider =
    matches(env.TURN_KEY_ID, KEY_ID_PATTERN) && matches(env.TURN_KEY_API_TOKEN, TOKEN_PATTERN) && dailyCapBytes !== null && ticketCapBytes !== null
      ? { keyId: env.TURN_KEY_ID, apiToken: env.TURN_KEY_API_TOKEN, dailyCapBytes, ticketCapBytes }
      : null;
  const analytics =
    matches(env.ANALYTICS_ACCOUNT_ID, ACCOUNT_PATTERN) && matches(env.ANALYTICS_API_TOKEN, TOKEN_PATTERN)
      ? { accountId: env.ANALYTICS_ACCOUNT_ID, apiToken: env.ANALYTICS_API_TOKEN }
      : null;
  return {
    environment: typeof env.ENVIRONMENT === "string" && /^[a-z]{1,16}$/.test(env.ENVIRONMENT) ? env.ENVIRONMENT : "development",
    limits,
    provider,
    analytics,
    flagCacheMilliseconds: integer(env.FLAG_CACHE_SECONDS, 5, 0, 60) * 1000,
  };
}

function integer(value: unknown, fallback: number, minimum: number, maximum: number): number {
  const text = typeof value === "number" ? String(value) : value;
  if (typeof text !== "string" || !/^(0|[1-9][0-9]{0,8})$/.test(text)) return fallback;
  const parsed = Number(text);
  return parsed >= minimum && parsed <= maximum ? parsed : fallback;
}

/** Decimal gigabytes such as "30" or "0.5", from 0.001 to 100000, as bytes; anything else is null. */
function gigabytes(value: unknown): number | null {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]{0,5})(\.[0-9]{1,3})?$/.test(value)) return null;
  const bytes = Math.round(Number(value) * GIGABYTE);
  return bytes >= GIGABYTE / 1000 && bytes <= 100_000 * GIGABYTE ? bytes : null;
}

function matches(value: unknown, pattern: RegExp): value is string {
  return typeof value === "string" && pattern.test(value);
}
