/** `GET /v1/status`: what clients may use right now, so they can degrade before they try. */
import { HEARTBEAT_SECONDS, LISTING_TTL_SECONDS } from "../objects/host-session";
import type { RequestContext } from "./context";
import { jsonResponse, requireNoBody } from "./http";

export const STATUS_SCHEMA = "topiaforge.control-plane-status/1";
export const API_VERSION = 1;

export async function status(context: RequestContext): Promise<Response> {
  requireNoBody(context.request);
  const ready = context.hasServiceKey();
  const flags = ready ? await context.flags() : null;
  const features = flags?.features ?? { hosting: false, invites: false, signaling: false, relay: false };
  const killSwitch = flags?.killSwitch.engaged ?? false;
  return jsonResponse(200, {
    schema: STATUS_SCHEMA,
    api: API_VERSION,
    features: {
      hosting: features.hosting,
      invites: features.invites,
      signaling: features.signaling,
      relay: features.relay && !killSwitch && context.config.provider !== null,
    },
    killSwitch,
    hosting: { heartbeatSeconds: HEARTBEAT_SECONDS, listingTtlSeconds: LISTING_TTL_SECONDS },
    relay: { credentialTtlSeconds: context.config.limits.credentialTtlSeconds },
  });
}
