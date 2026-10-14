/**
 * Reads relay usage from Cloudflare's GraphQL Analytics API (dataset `callsTurnUsageAdaptiveGroups`): the day's total
 * egress for the TURN key, and the heaviest credentials by egress, so the spend guard can trip the kill switch and
 * revoke a credential that relays far more than it planned to.
 */
import type { Analytics } from "../config";
import { UpstreamError, boundedJson, call } from "./turn-api";

const GRAPHQL_URL = "https://api.cloudflare.com/client/v4/graphql";
const MAXIMUM_RESPONSE_BYTES = 256 * 1024;
export const HEAVIEST_CREDENTIALS = 100;

const QUERY = `query TurnUsage($accountTag: string!, $keyId: string!, $day: Date!) {
  viewer {
    accounts(filter: { accountTag: $accountTag }) {
      total: callsTurnUsageAdaptiveGroups(limit: 1, filter: { keyId: $keyId, date_geq: $day, date_leq: $day }) {
        sum { egressBytes }
      }
      heaviest: callsTurnUsageAdaptiveGroups(limit: ${HEAVIEST_CREDENTIALS}, filter: { keyId: $keyId, date_geq: $day, date_leq: $day }, orderBy: [sum_egressBytes_DESC]) {
        dimensions { username }
        sum { egressBytes }
      }
    }
  }
}`;

export interface TurnUsage {
  readonly egressBytes: number;
  readonly byUsername: ReadonlyMap<string, number>;
}

/** The TURN key's egress for one UTC day ("YYYY-MM-DD"). Throws UpstreamError on any failure or surprise. */
export async function readTurnUsage(analytics: Analytics, keyId: string, day: string): Promise<TurnUsage> {
  const response = await call(
    new Request(GRAPHQL_URL, {
      method: "POST",
      headers: { authorization: `Bearer ${analytics.apiToken}`, "content-type": "application/json" },
      body: JSON.stringify({ query: QUERY, variables: { accountTag: analytics.accountId, keyId, day } }),
    }),
  );
  if (response.status !== 200) throw new UpstreamError(`status ${response.status}`);
  return readUsage(await boundedJson(response, MAXIMUM_RESPONSE_BYTES));
}

export function readUsage(value: unknown): TurnUsage {
  if (!isRecord(value)) throw new UpstreamError("shape");
  const errors = value["errors"];
  if (errors !== undefined && errors !== null && !(Array.isArray(errors) && errors.length === 0)) throw new UpstreamError("query errors");
  const accounts = path(value, ["data", "viewer", "accounts"]);
  if (!Array.isArray(accounts) || accounts.length !== 1 || !isRecord(accounts[0])) throw new UpstreamError("accounts");
  const account = accounts[0];
  const total = account["total"];
  const heaviest = account["heaviest"];
  if (!Array.isArray(total) || total.length > 1 || !Array.isArray(heaviest) || heaviest.length > HEAVIEST_CREDENTIALS) {
    throw new UpstreamError("groups");
  }
  const egressBytes = total.length === 0 ? 0 : bytesOf(total[0]);
  const byUsername = new Map<string, number>();
  for (const group of heaviest) {
    const username = path(group, ["dimensions", "username"]);
    if (typeof username !== "string" || username.length === 0 || username.length > 512) throw new UpstreamError("username");
    byUsername.set(username, bytesOf(group));
  }
  return { egressBytes, byUsername };
}

function bytesOf(group: unknown): number {
  const bytes = path(group, ["sum", "egressBytes"]);
  if (typeof bytes !== "number" || !Number.isSafeInteger(bytes) || bytes < 0) throw new UpstreamError("egress");
  return bytes;
}

function path(value: unknown, keys: readonly string[]): unknown {
  let current = value;
  for (const key of keys) {
    if (!isRecord(current)) return undefined;
    current = current[key];
  }
  return current;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
