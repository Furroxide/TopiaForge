/**
 * The TopiaForge multiplayer control plane (docs/internal/ControlPlaneApi.md): a Cloudflare Worker that routes the
 * versioned HTTP and WebSocket API to its Durable Objects, and an hourly cron that measures relay spend.
 *
 * Routing is exact: unknown paths are 404, known paths with the wrong method are 405, and any failure a handler did
 * not anticipate is a bare 500 that names nothing.
 */
import * as admin from "./api/admin";
import { RequestContext } from "./api/context";
import * as hosts from "./api/hosts";
import { ApiError, errorResponse } from "./api/http";
import * as invites from "./api/invites";
import * as servers from "./api/servers";
import * as sessions from "./api/sessions";
import { status } from "./api/status";
import type { Env } from "./env";

export { DirectoryShard } from "./objects/directory-shard";
export { HostSession } from "./objects/host-session";
export { InviteShard } from "./objects/invite-shard";
export { RateLimiter } from "./objects/rate-limiter";
export { SpendGuard } from "./objects/spend-guard";

type Handler = (context: RequestContext, ...parameters: string[]) => Promise<Response>;

interface Route {
  readonly pattern: RegExp;
  readonly methods: Readonly<Partial<Record<string, Handler>>>;
  /** Only the server browser takes a query string. */
  readonly query?: boolean;
}

const ID = "([0-9a-f]{64})";
const CODE = "([0-9A-Z]{10})";

export const ROUTES: readonly Route[] = [
  { pattern: /^\/v1\/status$/, methods: { GET: status } },
  { pattern: /^\/v1\/hosts\/challenge$/, methods: { POST: hosts.challenge } },
  { pattern: /^\/v1\/hosts$/, methods: { POST: hosts.register } },
  { pattern: new RegExp(`^/v1/hosts/${ID}$`), methods: { GET: hosts.hostView, DELETE: hosts.endHost } },
  { pattern: new RegExp(`^/v1/hosts/${ID}/heartbeat$`), methods: { POST: hosts.heartbeat } },
  { pattern: new RegExp(`^/v1/hosts/${ID}/descriptor$`), methods: { PUT: hosts.updateDescriptor } },
  { pattern: new RegExp(`^/v1/hosts/${ID}/invites$`), methods: { POST: hosts.createInvite } },
  { pattern: new RegExp(`^/v1/hosts/${ID}/invites/${CODE}$`), methods: { DELETE: hosts.revokeInvite } },
  { pattern: /^\/v1\/servers$/, methods: { GET: servers.listServers }, query: true },
  { pattern: new RegExp(`^/v1/servers/${ID}$`), methods: { GET: servers.serverDetail } },
  { pattern: /^\/v1\/invites\/resolve$/, methods: { POST: invites.resolveInvite } },
  { pattern: /^\/v1\/join-tickets$/, methods: { POST: invites.createJoinTicket } },
  { pattern: new RegExp(`^/v1/signal/${ID}$`), methods: { GET: sessions.openSignaling } },
  { pattern: /^\/v1\/turn\/credentials$/, methods: { POST: sessions.mintRelayCredentials } },
  { pattern: /^\/v1\/admin\/state$/, methods: { GET: admin.adminState } },
  { pattern: /^\/v1\/admin\/kill-switch$/, methods: { PUT: admin.setKillSwitch } },
  { pattern: /^\/v1\/admin\/features$/, methods: { PUT: admin.setFeatures } },
  { pattern: new RegExp(`^/v1/admin/servers/${ID}/delist$`), methods: { POST: admin.delist } },
  { pattern: /^\/v1\/admin\/spend\/measure$/, methods: { POST: admin.measureSpend } },
];

export async function handle(request: Request, env: Env): Promise<Response> {
  let context: RequestContext;
  try {
    context = new RequestContext(request, env);
  } catch {
    return errorResponse("invalid-request");
  }
  for (const route of ROUTES) {
    const match = route.pattern.exec(context.url.pathname);
    if (match === null) continue;
    const handler = route.methods[request.method];
    if (handler === undefined) return errorResponse("method-not-allowed", undefined, { allow: Object.keys(route.methods).join(", ") });
    if (!route.query && context.url.search !== "") return errorResponse("invalid-request");
    try {
      return await handler(context, ...match.slice(1));
    } catch (error) {
      if (error instanceof ApiError) return errorResponse(error.code, error.retryAfterSeconds);
      console.error("control-plane: unhandled failure", error instanceof Error ? error.name : "unknown");
      return errorResponse("internal");
    }
  }
  return errorResponse("not-found");
}

export default {
  fetch(request: Request, env: Env): Promise<Response> {
    return handle(request, env);
  },

  async scheduled(_controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    const guard = env.SPEND_GUARD.get(env.SPEND_GUARD.idFromName("spend-guard"));
    ctx.waitUntil(guard.measure().then(() => undefined));
  },
} satisfies ExportedHandler<Env>;
