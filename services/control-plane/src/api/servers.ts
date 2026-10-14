/**
 * The server browser: public listings for one game build and protocol major, paged in server id order across the
 * 16 directory shards, and a public server's signed descriptor. Invite-only servers are found only through invites.
 */
import { isJsonObject, parse, type JsonObject } from "../shared/canonical-json";
import { isGameBuild } from "../shared/descriptor";
import { DIRECTORY_SHARDS, directoryShardName } from "../objects/directory-shard";
import type { RequestContext } from "./context";
import { ApiError, jsonResponse } from "./http";
import { SERVER_ID_PATTERN } from "./validate";

export const DEFAULT_PAGE = 50;
export const MAXIMUM_PAGE = 100;
export const BROWSE_LIMITS = [
  { window: "minute", limit: 60 },
  { window: "hour", limit: 1200 },
] as const;
const QUERY_KEYS = ["build", "protocol", "after", "limit"];

export async function listServers(context: RequestContext): Promise<Response> {
  const query = context.url.searchParams;
  const keys = [...query.keys()];
  if (keys.some((key) => !QUERY_KEYS.includes(key)) || new Set(keys).size !== keys.length) throw new ApiError("invalid-request");
  const build = query.get("build");
  const protocol = query.get("protocol");
  const after = query.get("after");
  const limitText = query.get("limit");
  if (!isGameBuild(build) || protocol === null || !/^(0|[1-9][0-9]{0,8})$/.test(protocol)) throw new ApiError("invalid-request");
  if (after !== null && !SERVER_ID_PATTERN.test(after)) throw new ApiError("invalid-request");
  if (limitText !== null && !/^[1-9][0-9]{0,2}$/.test(limitText)) throw new ApiError("invalid-request");
  const limit = limitText === null ? DEFAULT_PAGE : Number(limitText);
  if (limit > MAXIMUM_PAGE) throw new ApiError("invalid-request");
  await context.limit("browse", BROWSE_LIMITS);

  // Shard i holds the ids that start with hexadecimal digit i, so shards in order give ids in order.
  const first = after === null ? 0 : parseInt(after[0]!, 16);
  const shards = Array.from({ length: DIRECTORY_SHARDS - first }, (_, offset) => first + offset);
  const pages = await Promise.all(shards.map((index) => context.directoryShard(directoryShardName(build, protocol, index)).page(after, limit)));
  const servers: JsonObject[] = [];
  let more = false;
  for (const page of pages) {
    for (const body of page.listings) {
      if (servers.length === limit) {
        more = true;
        break;
      }
      servers.push(readListing(body));
    }
    if (page.more) more = true;
  }
  const next = more && servers.length > 0 ? (servers[servers.length - 1]!["serverId"] as string) : null;
  return jsonResponse(200, { servers, next });
}

export async function serverDetail(context: RequestContext, serverId: string): Promise<Response> {
  const description = await context.hostSession(serverId).describe(serverId);
  if (description === null || description.visibility !== "public") throw new ApiError("not-found");
  return jsonResponse(200, {
    listing: readListing(description.listing),
    descriptor: description.descriptor,
    descriptorSha256: description.descriptorSha256,
  });
}

export function readListing(body: string): JsonObject {
  const parsed = parse(new TextEncoder().encode(body), 16 * 1024);
  if (!parsed.ok || !isJsonObject(parsed.value)) throw new Error("A stored listing is not canonical JSON.");
  return parsed.value;
}
