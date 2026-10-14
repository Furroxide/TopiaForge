/**
 * The Worker's bindings, vars and secrets. wrangler.toml declares the bindings and vars; secrets are set out of band
 * with `wrangler secret put` (docs/internal/ControlPlaneOperations.md). Every secret is optional here because a
 * missing one must make its feature fail closed, never crash the Worker.
 */
import type { DirectoryShard } from "./objects/directory-shard";
import type { HostSession } from "./objects/host-session";
import type { InviteShard } from "./objects/invite-shard";
import type { RateLimiter } from "./objects/rate-limiter";
import type { SpendGuard } from "./objects/spend-guard";

export interface Env {
  readonly HOST_SESSIONS: DurableObjectNamespace<HostSession>;
  readonly DIRECTORY_SHARDS: DurableObjectNamespace<DirectoryShard>;
  readonly INVITE_SHARDS: DurableObjectNamespace<InviteShard>;
  readonly SPEND_GUARD: DurableObjectNamespace<SpendGuard>;
  readonly RATE_LIMITER: DurableObjectNamespace<RateLimiter>;

  /** "development", "staging" or "production". */
  readonly ENVIRONMENT?: string;

  /** Base64url of 32 to 64 random bytes. Every MAC and rate-limit key derives from it. */
  readonly SERVICE_KEY?: string;
  /** Bearer token for /v1/admin, at least 32 characters. Admin is disabled without it. */
  readonly ADMIN_TOKEN?: string;
  /** The Cloudflare Realtime TURN key id and its API token. Relay is unavailable without them. */
  readonly TURN_KEY_ID?: string;
  readonly TURN_KEY_API_TOKEN?: string;
  /** Account id and an Account Analytics read token for the hourly TURN usage measurement. */
  readonly ANALYTICS_ACCOUNT_ID?: string;
  readonly ANALYTICS_API_TOKEN?: string;
  /** Spend ceilings in decimal gigabytes. Relay is unavailable until both are set. */
  readonly TURN_DAILY_CAP_GB?: string;
  readonly TURN_TICKET_CAP_GB?: string;

  /**
   * The ranges the relay provider allocates relay addresses from, as the operator lists them: CIDR blocks separated by
   * single spaces. A relay-only side's candidates must lie inside them, and relay is unavailable until they parse.
   */
  readonly RELAY_ADDRESS_RANGES?: string;

  readonly TURN_CREDENTIAL_TTL_SECONDS?: string;
  readonly TURN_IP_MINTS_PER_HOUR?: string;
  readonly TURN_IP_MINTS_PER_MINUTE?: string;
  readonly TURN_TICKET_MAX_MINTS?: string;
  readonly TURN_MEASUREMENT_FAILURE_LIMIT?: string;
  readonly FLAG_CACHE_SECONDS?: string;
}
