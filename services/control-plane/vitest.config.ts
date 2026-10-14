import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

// Every secret below is a test-only placeholder. Real values are set out of band with `wrangler secret put`
// (docs/internal/ControlPlaneOperations.md); nothing here reaches a deployment.
export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.toml" },
      miniflare: {
        bindings: {
          ENVIRONMENT: "test",
          // base64url("test-only service key, not real!")
          SERVICE_KEY: "dGVzdC1vbmx5IHNlcnZpY2Uga2V5LCBub3QgcmVhbCE",
          ADMIN_TOKEN: "test-only-admin-token-not-a-real-secret",
          TURN_KEY_ID: "test-turn-key-id",
          TURN_KEY_API_TOKEN: "test-turn-api-token",
          ANALYTICS_ACCOUNT_ID: "00000000000000000000000000000000",
          ANALYTICS_API_TOKEN: "test-analytics-api-token",
          TURN_DAILY_CAP_GB: "2",
          TURN_TICKET_CAP_GB: "1",
          FLAG_CACHE_SECONDS: "0",
        },
      },
    }),
  ],
  test: {
    include: ["test/**/*.test.ts"],
  },
});
