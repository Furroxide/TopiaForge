import {
  env,
  runInDurableObject,
  createExecutionContext,
  waitOnExecutionContext,
} from "cloudflare:test";
import { beforeEach, afterEach, describe, expect, it } from "vitest";
import { startScenario, endScenario, type Scenario } from "./support/scenario";
import { call, identity, register } from "./support/api";
import { nowSeconds } from "../src/shared/runtime";
import { ActivityReporter } from "../src/activity";
import worker from "../src/index";

let scenario: Scenario;
beforeEach(() => {
  scenario = startScenario("activity");
});
afterEach(endScenario);
const shard = (index = 0) =>
  env.ACTIVITY_SHARDS.get(env.ACTIVITY_SHARDS.idFromName(`activity/${index}`));
const snapshots = () =>
  env.ACTIVITY_SNAPSHOTS.get(env.ACTIVITY_SNAPSHOTS.idFromName("snapshots"));
const monitorToken = "test-only-activity-token-not-a-real-secret";

describe("activity leases and publication", () => {
  it("replaces duplicate leases, rejects reordered heartbeats, honors ends and expires abandoned hosts", async () => {
    const s = shard(),
      serverId = "0".repeat(64),
      version = scenario.clock.now();
    await s.apply({
      serverId,
      version,
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    await s.apply({
      serverId,
      version,
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    expect(await s.totals(nowSeconds())).toEqual({ sessions: 1, players: 3 });
    await s.apply({ serverId, version: version + 1, expiresAt: 0, players: 0 });
    await s.apply({
      serverId,
      version,
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    expect(await s.totals(nowSeconds())).toEqual({ sessions: 0, players: 0 });
    scenario.clock.advance(1);
    await s.apply({
      serverId,
      version: scenario.clock.now(),
      expiresAt: nowSeconds() + 90,
      players: 4,
    });
    expect(await s.totals(nowSeconds())).toEqual({ sessions: 1, players: 4 });
    scenario.clock.advance(90);
    expect(await s.totals(nowSeconds())).toEqual({ sessions: 0, players: 0 });
  });
  it("includes public and invite-only sessions across builds, and updates player counts from heartbeat", async () => {
    const a = await identity(`activity-public-${scenario.day}`),
      b = await identity(`activity-private-${scenario.day}`);
    const ra = await register(a, scenario.ip),
      rb = await register(b, scenario.ip, {
        visibility: "invite",
        gameBuild: "2597",
      });
    scenario.clock.advance(6);
    expect(
      (
        await call("POST", `/v1/hosts/${a.serverId}/heartbeat`, {
          token: ra.token,
          ip: scenario.ip,
          body: { players: 3 },
        })
      ).status,
    ).toBe(200);
    expect(
      (
        await call("POST", `/v1/hosts/${b.serverId}/heartbeat`, {
          token: rb.token,
          ip: scenario.ip,
          body: { players: 4 },
        })
      ).status,
    ).toBe(200);
    // Flushing explicitly waits for outboxes without relying on fire-and-forget timing in the caller.
    for (const host of [a, b])
      await runInDurableObject(
        env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId)),
        async (instance) => {
          await (
            instance as unknown as { activity: ActivityReporter }
          ).activity.settled();
        },
      );
    const totals = await Promise.all(
      Array.from({ length: 16 }, (_, i) => shard(i).totals(nowSeconds())),
    );
    expect(totals.reduce((n, r) => n + r.sessions, 0)).toBe(2);
    expect(totals.reduce((n, r) => n + r.players, 0)).toBe(7);
    await call("DELETE", `/v1/hosts/${b.serverId}`, {
      token: rb.token,
      ip: scenario.ip,
    });
    for (const host of [a, b])
      await runInDurableObject(
        env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId)),
        async (instance) => {
          await (
            instance as unknown as { activity: ActivityReporter }
          ).activity.settled();
        },
      );
  });
  it("suppresses small populations before storage and waits fifteen minutes before any read", async () => {
    const snapshot = snapshots();
    await snapshot.collect();
    scenario.clock.advance(300);
    await shard().apply({
      serverId: "0".repeat(64),
      version: scenario.clock.now(),
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    await snapshot.collect();
    expect(await snapshot.released()).toBeNull();
    scenario.clock.advance(900);
    expect(await snapshot.released()).toMatchObject({
      state: "suppressed",
      sessions: null,
      players: null,
    });
    const stored = await runInDurableObject(snapshot, (_instance, state) =>
      state.storage.sql
        .exec("SELECT sessions,players FROM snapshot WHERE state='suppressed'")
        .toArray(),
    );
    expect(
      stored.every((r) => r["sessions"] === null && r["players"] === null),
    ).toBe(true);
  });
  it("publishes only aggregates at the threshold and never raw session keys", async () => {
    const snapshot = snapshots();
    await snapshot.collect();
    scenario.clock.advance(300);
    for (let i = 0; i < 5; i++)
      await shard(i).apply({
        serverId: i.toString(16).repeat(64),
        version: scenario.clock.now(),
        expiresAt: nowSeconds() + 90,
        players: 2,
      });
    await snapshot.collect();
    scenario.clock.advance(900);
    const response = await call("GET", "/v1/monitor/activity", {
      token: monitorToken,
      ip: scenario.ip,
    });
    expect(response.status).toBe(200);
    expect(response.json).toMatchObject({
      state: "available",
      sessions: 5,
      players: 10,
    });
    expect(Object.keys(response.json).sort()).toEqual([
      "players",
      "sampledAt",
      "schema",
      "sessions",
      "state",
    ]);
  });
  it("uses a separate narrow credential and leaves /v1/status unchanged", async () => {
    for (const token of [
      undefined,
      "test-only-admin-token-not-a-real-secret",
      "wrong",
    ]) {
      const response = await call("GET", "/v1/monitor/activity", {
        ...(token === undefined ? {} : { token }),
        ip: scenario.ip,
      });
      expect(response.status).toBe(401);
    }
    const response = await call("GET", "/v1/status", { ip: scenario.ip });
    expect(response.json["schema"]).toBe("topiaforge.control-plane-status/1");
    expect(response.json).not.toHaveProperty("activity");
  });
  it("records unavailable rather than partial or zero counts when a shard cannot answer", async () => {
    const snapshot = snapshots();
    await snapshot.collect();
    scenario.clock.advance(300);
    await runInDurableObject(shard(), (instance) => {
      instance.totals = () => {
        throw new Error("test-only aggregate failure");
      };
    });
    try {
      await snapshot.collect();
    } finally {
      await runInDurableObject(shard(), (instance) => {
        Reflect.deleteProperty(instance, "totals");
      });
    }
    scenario.clock.advance(900);
    expect(await snapshot.released()).toMatchObject({
      state: "unavailable",
      sessions: null,
      players: null,
    });
  });
  it("bounds a failing outbox to three attempts and never throws from a recording operation", async () => {
    const host = await identity(`failed-analytics-${scenario.day}`);
    const stub = env.HOST_SESSIONS.get(
      env.HOST_SESSIONS.idFromName(host.serverId),
    );
    await runInDurableObject(stub, async (_instance, state) => {
      const failing = {
        ...env,
        ACTIVITY_SHARDS: {
          idFromName() {
            return "test";
          },
          get() {
            throw new Error("test-only delivery failure");
          },
        },
      } as unknown as typeof env;
      const reporter = new ActivityReporter(state, failing);
      expect(() =>
        reporter.record({
          serverId: host.serverId,
          expiresAt: nowSeconds() + 90,
          players: 3,
        } as never),
      ).not.toThrow();
      await reporter.settled();
      for (const seconds of [5, 15, 30]) {
        scenario.clock.advance(seconds);
        await reporter.flush().catch(() => undefined);
      }
      expect(
        state.storage.sql
          .exec<{ attempts: number }>("SELECT attempts FROM activity_outbox")
          .one().attempts,
      ).toBe(3);
    });
  });
  it("excludes configured synthetic sessions and bounds analytics failures without failing host operations", async () => {
    const host = await identity(`activity-synthetic-${scenario.day}`);
    const session = await register(host, scenario.ip, { visibility: "invite" });
    await runInDurableObject(
      env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId)),
      async (_instance, state) => {
        const row = state.storage.sql
          .exec<{ expires_at: number }>("SELECT expires_at FROM session")
          .one();
        const reporter = new ActivityReporter(state, {
          ...env,
          MONITOR_SERVER_IDS: host.serverId,
        });
        reporter.record({
          serverId: host.serverId,
          expiresAt: row.expires_at,
          players: 16,
        } as never);
        await reporter.settled();
      },
    );
    const total = await shard(parseInt(host.serverId[0]!, 16)).totals(
      nowSeconds(),
    );
    expect(total.sessions).toBe(0);
    const response = await call("DELETE", `/v1/hosts/${host.serverId}`, {
      token: session.token,
      ip: scenario.ip,
    });
    expect(response.status).toBe(204);
    await runInDurableObject(
      env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId)),
      async (instance) => {
        await (
          instance as unknown as { activity: ActivityReporter }
        ).activity.settled();
      },
    );
  });
  it("keeps activity collection separate from the hourly spend measurement", async () => {
    const ctx = createExecutionContext();
    await worker.scheduled(
      {
        cron: "*/5 * * * *",
        scheduledTime: scenario.clock.now(),
        noRetry() {},
      } as ScheduledController,
      env,
      ctx,
    );
    await waitOnExecutionContext(ctx);
    expect(scenario.relay.authorizations).toHaveLength(0);
    expect(await snapshots().released()).toBeNull();
  });
});
