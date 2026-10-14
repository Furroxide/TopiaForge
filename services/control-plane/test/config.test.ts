import { describe, expect, it } from "vitest";
import wranglerToml from "../wrangler.toml?raw";
import { readConfig } from "../src/config";
import type { Env } from "../src/env";
import * as worker from "../src/index";

const CLASSES = ["HostSession", "DirectoryShard", "InviteShard", "SpendGuard", "RateLimiter"];
const BINDINGS: Record<string, string> = {
  HOST_SESSIONS: "HostSession",
  DIRECTORY_SHARDS: "DirectoryShard",
  INVITE_SHARDS: "InviteShard",
  SPEND_GUARD: "SpendGuard",
  RATE_LIMITER: "RateLimiter",
};

function env(values: Partial<Record<keyof Env, string>>): Env {
  return values as unknown as Env;
}

describe("configuration", () => {
  it("falls back to safe defaults for missing or out-of-range limits", () => {
    const config = readConfig(env({ TURN_CREDENTIAL_TTL_SECONDS: "59", TURN_IP_MINTS_PER_HOUR: "abc", TURN_TICKET_MAX_MINTS: "07" }));
    expect(config.limits).toMatchObject({ credentialTtlSeconds: 600, ipMintsPerHour: 20, ipMintsPerMinute: 5, ticketMaxMints: 36, measurementFailureLimit: 3 });
    expect(readConfig(env({ TURN_CREDENTIAL_TTL_SECONDS: "3600" })).limits.credentialTtlSeconds).toBe(3600);
    expect(readConfig(env({ TURN_MEASUREMENT_FAILURE_LIMIT: "0" })).limits.measurementFailureLimit).toBe(0);
    expect(readConfig(env({ ENVIRONMENT: "Prod!" })).environment).toBe("development");
  });

  it("leaves relay unavailable until the key, its token and both spend ceilings are set", () => {
    const complete = { TURN_KEY_ID: "test-turn-key-id", TURN_KEY_API_TOKEN: "test-turn-api-token", TURN_DAILY_CAP_GB: "0.5", TURN_TICKET_CAP_GB: "0.25" };
    expect(readConfig(env(complete)).provider).toMatchObject({ dailyCapBytes: 500_000_000, ticketCapBytes: 250_000_000 });
    for (const missing of Object.keys(complete)) {
      const partial = { ...complete } as Record<string, string>;
      delete partial[missing];
      expect(readConfig(env(partial)).provider, missing).toBeNull();
    }
    for (const cap of ["0", "-1", "1e3", "1.2345", "999999", ""]) expect(readConfig(env({ ...complete, TURN_DAILY_CAP_GB: cap })).provider, cap).toBeNull();
    expect(readConfig(env({ ANALYTICS_ACCOUNT_ID: "not-an-account", ANALYTICS_API_TOKEN: "test-analytics-api-token" })).analytics).toBeNull();
  });
});

describe("wrangler.toml", () => {
  const sections = wranglerToml.split(/^\[env\.(staging|production)\]$/m);

  it("declares every Durable Object class once, as SQLite-backed, in one migration", () => {
    expect(wranglerToml).toContain(`new_sqlite_classes = [${CLASSES.map((name) => `"${name}"`).join(", ")}]`);
    expect(wranglerToml.match(/^\[\[migrations\]\]$/gm)).toHaveLength(1);
    for (const name of CLASSES) expect(typeof (worker as Record<string, unknown>)[name]).toBe("function");
  });

  it("binds the same objects and the same vars in every environment, apart from its name", () => {
    expect(sections).toHaveLength(5);
    const [top, , staging, , production] = sections as [string, string, string, string, string];
    const bindings = (text: string) => [...text.matchAll(/name = "([A-Z_]+)"\nclass_name = "([A-Za-z]+)"/g)].map((match) => `${match[1]}=${match[2]}`).sort();
    const vars = (text: string) =>
      [...text.matchAll(/^([A-Z_]+) = "([^"]*)"$/gm)].filter((match) => match[1] !== "ENVIRONMENT").map((match) => `${match[1]}=${match[2]}`).sort();
    const expected = Object.entries(BINDINGS).map(([binding, name]) => `${binding}=${name}`).sort();
    for (const [label, text] of [["top level", top], ["staging", staging], ["production", production]] as const) {
      expect(bindings(text), label).toEqual(expected);
      expect(vars(text), label).toEqual(vars(top));
      expect(text, label).toMatch(/observability\]\nenabled = false/);
    }
    expect(staging).toContain('ENVIRONMENT = "staging"');
    expect(production).toContain('ENVIRONMENT = "production"');
  });

  it("keeps account details and secrets out of the file", () => {
    expect(wranglerToml).not.toMatch(/^\s*account_id\s*=/m);
    for (const secret of ["SERVICE_KEY", "ADMIN_TOKEN", "TURN_KEY_ID", "TURN_KEY_API_TOKEN", "ANALYTICS_API_TOKEN", "ANALYTICS_ACCOUNT_ID", "TURN_DAILY_CAP_GB", "TURN_TICKET_CAP_GB"]) {
      expect(wranglerToml).not.toMatch(new RegExp(`^${secret}\\s*=`, "m"));
    }
    expect(wranglerToml).toContain('crons = ["7 * * * *"]');
  });
});
