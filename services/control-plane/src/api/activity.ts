import type { RequestContext } from "./context";
import { ApiError, jsonResponse, requireNoBody } from "./http";
import { sha256, utf8 } from "../shared/wire-text";

export async function activity(context: RequestContext): Promise<Response> {
  requireNoBody(context.request);
  const expected = context.env.MONITOR_ACTIVITY_TOKEN;
  const presented = context.request.headers.get("authorization") ?? "";
  if (typeof expected !== "string" || expected.length < 32)
    throw new ApiError("unauthorized");
  const [a, b] = await Promise.all([
    sha256(utf8(`Bearer ${expected}`)),
    sha256(utf8(presented)),
  ]);
  if (!crypto.subtle.timingSafeEqual(a, b)) throw new ApiError("unauthorized");
  await context.limit("activity", [
    { window: "minute", limit: 6 },
    { window: "hour", limit: 120 },
  ]);
  const snapshot = await context.env.ACTIVITY_SNAPSHOTS.get(
    context.env.ACTIVITY_SNAPSHOTS.idFromName("snapshots"),
  ).released();
  // A rolling upgrade may still reach a legacy snapshot instance.
  if (snapshot === null || snapshot.schema !== "topiaforge.activity/2" || snapshot.scope !== "public")
    throw new ApiError("service-unavailable");
  return jsonResponse(200, { ...snapshot });
}
