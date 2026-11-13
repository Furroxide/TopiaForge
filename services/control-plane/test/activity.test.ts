import {
  env,
  runInDurableObject,
  runDurableObjectAlarm,
  createExecutionContext,
  waitOnExecutionContext,
} from "cloudflare:test";
import { beforeEach, afterEach, describe, expect, it } from "vitest";
import { startScenario, endScenario, type Scenario } from "./support/scenario";
import { call, identity, register } from "./support/api";
import { nowSeconds } from "../src/shared/runtime";
import { ACTIVITY_SCHEMA, ActivityReporter, activityEnabled } from "../src/activity";
import { HostStore } from "../src/objects/host-store";
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
  it("waits fifteen minutes after the last shard finishes collecting", async () => {
    const snapshot = snapshots();
    await snapshot.collect();
    scenario.clock.advance(300);
    await runInDurableObject(snapshot, async (instance) => {
      const holder = instance as unknown as { env: typeof env };
      const original = holder.env;
      let release!: () => void, arrived!: () => void;
      const ready = new Promise<void>((r) => {
          arrived = r;
        }),
        gate = new Promise<void>((r) => {
          release = r;
        });
      holder.env = {
        ...original,
        ACTIVITY_SHARDS: {
          idFromName() {
            return "test";
          },
          get() {
            return {
              async totals() {
                arrived();
                await gate;
                return { scope: "public-v2", sessions: 1, players: 2 };
              },
            };
          },
        },
      } as unknown as typeof env;
      try {
        const collecting = instance.collect();
        await ready;
        scenario.clock.advance(300);
        release();
        await collecting;
        scenario.clock.advance(600);
        const released = instance.released();
        assertNoPrematureSnapshot(
          released,
          Math.floor((scenario.clock.now() / 1000 - 900) / 300) * 300,
        );
        scenario.clock.advance(300);
        expect(instance.released()?.state).toBe("available");
      } finally {
        holder.env = original;
        release();
      }
    });
  });
  it("does not release stale snapshots when collection stops", async () => {
    const snapshot = snapshots();
    await snapshot.collect();
    scenario.clock.advance(300);
    await snapshot.collect();
    scenario.clock.advance(86401);
    expect(await snapshot.released()).toBeNull();
  });
  it("replaces duplicate leases, rejects reordered heartbeats, honors ends and expires abandoned hosts", async () => {
    const s = shard(),
      serverId = "0".repeat(64),
      version = scenario.clock.now();
    await s.apply({ scope: "public-v2",
      serverId,
      version,
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    await s.apply({ scope: "public-v2",
      serverId,
      version,
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    expect(await s.totals(nowSeconds())).toEqual({ scope: "public-v2", sessions: 1, players: 3 });
    await s.apply({ scope: "public-v2", serverId, version: version + 1, expiresAt: 0, players: 0 });
    await s.apply({ scope: "public-v2",
      serverId,
      version,
      expiresAt: nowSeconds() + 90,
      players: 3,
    });
    expect(await s.totals(nowSeconds())).toEqual({ scope: "public-v2", sessions: 0, players: 0 });
    scenario.clock.advance(1);
    await s.apply({ scope: "public-v2",
      serverId,
      version: scenario.clock.now(),
      expiresAt: nowSeconds() + 90,
      players: 4,
    });
    expect(await s.totals(nowSeconds())).toEqual({ scope: "public-v2", sessions: 1, players: 4 });
    scenario.clock.advance(90);
    expect(await s.totals(nowSeconds())).toEqual({ scope: "public-v2", sessions: 0, players: 0 });
  });
  it("excludes invite-only sessions across builds, and updates player counts from heartbeat", async () => {
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
    expect(totals.reduce((n, r) => n + r.sessions, 0)).toBe(1);
    expect(totals.reduce((n, r) => n + r.players, 0)).toBe(3);
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
    await shard().apply({ scope: "public-v2",
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
      sessionsBand: null,
      playersBand: null,
    });
    const stored = await runInDurableObject(snapshot, (_instance, state) =>
      state.storage.sql
        .exec("SELECT sessions,players FROM public_snapshot_v2 WHERE state='suppressed'")
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
      await shard(i).apply({ scope: "public-v2",
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
      schema: "topiaforge.activity/2",
      scope: "public",
      sessionsBand: { min: 5, max: 9 },
      playersBand: { min: 10, max: 19 },
    });
    expect(Object.keys(response.json).sort()).toEqual([
      "playersBand",
      "sampledAt",
      "schema",
      "scope",
      "sessionsBand",
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
    await collectWithShardTotals(() => {
      throw new Error("test-only aggregate failure");
    });
    scenario.clock.advance(900);
    expect(await snapshot.released()).toMatchObject({
      state: "unavailable",
      sessionsBand: null,
      playersBand: null,
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
          visibility: "public",
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
          .exec<{ attempts: number }>("SELECT attempts FROM public_activity_outbox_v2")
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
          visibility: "public",
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
  it("never promotes old combined rows or unqualified writes to public activity", async () => {
    const s = shard();
    // Assert the real method's refusal inside the object: the test RPC wrapper
    // reports deliberate remote rejections as additional unhandled errors.
    await runInDurableObject(s, async (instance) => {
      await expect(instance.apply({ serverId: "0".repeat(64), version: scenario.clock.now(),
        expiresAt: nowSeconds() + 90, players: 4 } as never)).rejects.toThrow("invalid public activity lease");
      expect(instance.totals(nowSeconds()).sessions).toBe(0);
    });
    const snapshot = snapshots();
    await runInDurableObject(snapshot, (_instance, state) => {
      state.storage.sql.exec("CREATE TABLE snapshot (sampled_at INTEGER, collected_at INTEGER, state TEXT, sessions INTEGER, players INTEGER)");
      state.storage.sql.exec("INSERT INTO snapshot VALUES (?,?, 'available',7,19)", nowSeconds()-1200, nowSeconds()-1200);
    });
    expect(await snapshot.released()).toBeNull();
  });
  it("stores only coarse public bands and rejects legacy shard totals during rollout", async () => {
    const snapshot = snapshots();
    await snapshot.collect();
    scenario.clock.advance(300);
    for (let i = 0; i < 7; i++) await shard(i).apply({ scope: "public-v2",
      serverId: i.toString(16).repeat(64), version: scenario.clock.now(),
      expiresAt: nowSeconds() + 90, players: i === 0 ? 7 : 2 });
    await snapshot.collect();
    const stored = await runInDurableObject(snapshot, (_instance, state) =>
      state.storage.sql.exec("SELECT sessions,players FROM public_snapshot_v2 WHERE state='available'").one());
    expect(stored).toMatchObject({ sessions: 5, players: 10 });
    scenario.clock.advance(900);
    expect(await snapshot.released()).toMatchObject({ scope: "public",
      sessionsBand: { min: 5, max: 9 }, playersBand: { min: 10, max: 19 } });
    await collectWithShardTotals(() => ({ sessions: 7, players: 19 }));
    scenario.clock.advance(900);
    expect(await snapshot.released()).toMatchObject({ state: "unavailable", sessionsBand: null, playersBand: null });
  });
  it("withdraws a public lease without reporting invite-only players", async () => {
    const host = await identity(`visibility-activity-${scenario.day}`);
    await runInDurableObject(env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId)), async (_instance, state) => {
      const reporter = new ActivityReporter(state, env);
      const session = { serverId: host.serverId, visibility: "public", expiresAt: nowSeconds()+90, players: 3 };
      reporter.record(session as never);
      await reporter.settled();
      expect((await shard(parseInt(host.serverId[0]!,16)).totals(nowSeconds())).players).toBe(3);
      reporter.record({ ...session, visibility: "invite", players: 4 } as never);
      await reporter.settled();
      expect((await shard(parseInt(host.serverId[0]!,16)).totals(nowSeconds())).players).toBe(0);
      expect(state.storage.sql.exec("SELECT players FROM public_activity_outbox_v2").one()["players"]).toBe(0);
      const withdrawal = state.storage.sql.exec("SELECT * FROM public_activity_outbox_v2").one();
      scenario.clock.advance(10);
      reporter.record({ ...session, visibility: "invite", players: 6 } as never);
      await reporter.settled();
      expect(state.storage.sql.exec("SELECT * FROM public_activity_outbox_v2").one()).toEqual(withdrawal);
      expect((await shard(parseInt(host.serverId[0]!,16)).totals(nowSeconds())).players).toBe(0);
    });
  });
  it("keeps the earliest shard cleanup deadline across staggered writes", async () => {
    const s = env.ACTIVITY_SHARDS.get(env.ACTIVITY_SHARDS.idFromName(`staggered-${scenario.day}`));
    const firstAt = nowSeconds();
    await s.apply({ scope: "public-v2", serverId: "0".repeat(64), version: scenario.clock.now(), expiresAt: 0, players: 0 });
    expect(await runInDurableObject(s, (_instance, state) => state.storage.getAlarm())).toBe((firstAt + 180) * 1000);
    scenario.clock.advance(179);
    await s.apply({ scope: "public-v2", serverId: "1".repeat(64), version: scenario.clock.now(), expiresAt: nowSeconds()+90, players: 3 });
    expect(await runInDurableObject(s, (_instance, state) => state.storage.getAlarm())).toBe((firstAt + 180) * 1000);
    scenario.clock.advance(1);
    await runInDurableObject(s, async (instance, state) => {
      await instance.alarm();
      const rows = state.storage.sql.exec("SELECT server_id FROM public_lease_v2").toArray();
      expect(rows).toEqual([{ server_id: "1".repeat(64) }]);
      expect(await state.storage.getAlarm()).toBe((firstAt + 359) * 1000);
    });
  });
  it("refuses legacy or non-public snapshot replies at the monitor endpoint", async () => {
    for (const reply of [
      { schema: "topiaforge.activity/1", sampledAt: nowSeconds()-900, state: "available", sessions: 7, players: 19 },
      { schema: "topiaforge.activity/2", scope: "combined", sampledAt: nowSeconds()-900, state: "available", sessionsBand: { min: 5, max: 9 }, playersBand: { min: 10, max: 19 } },
      { schema: "topiaforge.activity/2", sampledAt: nowSeconds()-900, state: "suppressed", sessionsBand: null, playersBand: null },
    ]) {
      // Inject the old response at the namespace boundary; own-instance method
      // overrides are intentionally inaccessible through Durable Object RPC.
      const mixed = {
        ...env,
        ACTIVITY_SNAPSHOTS: {
          idFromName() { return "test-only-snapshot"; },
          get() { return { released: async () => reply }; },
        },
      } as unknown as typeof env;
      const response = await worker.fetch(new Request("https://control-plane.example.invalid/v1/monitor/activity", {
        headers: { authorization: `Bearer ${monitorToken}`, "cf-connecting-ip": scenario.ip },
      }), mixed);
      expect(response.status).toBe(503);
      const body = await response.json();
      expect(body).not.toHaveProperty("sessions");
      expect(body).not.toHaveProperty("sessionsBand");
    }
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
  it("enables activity only for the public-only band format", () => {
    expect(ACTIVITY_SCHEMA).toBe("topiaforge.activity/2");
    expect(activityEnabled(env)).toBe(true);
    expect(activityEnabled(env, "topiaforge.activity/1")).toBe(false);
    expect(activityEnabled(env, "topiaforge.activity/3")).toBe(false);
  });
  it("reads any stored visibility other than public as invite-only and withdraws its lease", async () => {
    const host = await identity(`activity-stored-visibility-${scenario.day}`);
    const { token } = await register(host, scenario.ip);
    const stub = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    const index = parseInt(host.serverId[0]!, 16);
    await settle(stub);
    expect((await shard(index).totals(nowSeconds())).sessions).toBe(1);
    await runInDurableObject(stub, (_instance, state) => {
      state.storage.sql.exec("UPDATE session SET visibility = 'unlisted'");
      expect(new HostStore(state.storage.sql).session()?.visibility).toBe("invite");
    });
    scenario.clock.advance(6);
    const heartbeat = await call("POST", `/v1/hosts/${host.serverId}/heartbeat`, {
      token,
      ip: scenario.ip,
      body: { players: 3 },
    });
    expect(heartbeat.status).toBe(200);
    await settle(stub);
    expect(await shard(index).totals(nowSeconds())).toEqual({ scope: "public-v2", sessions: 0, players: 0 });
  });
  it("prepares the outbox once per object, and again after its storage is wiped", async () => {
    const host = await identity(`activity-outbox-once-${scenario.day}`);
    const stub = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    await runInDurableObject(stub, async (_instance, state) => {
      const statements: string[] = [];
      const sql = state.storage.sql;
      const counting = {
        storage: {
          sql: {
            exec: (query: string, ...bindings: SqlStorageValue[]) => {
              statements.push(query);
              return sql.exec(query, ...bindings);
            },
          },
          getAlarm: () => state.storage.getAlarm(),
          setAlarm: (time: number) => state.storage.setAlarm(time),
        },
        waitUntil: (promise: Promise<unknown>) => state.waitUntil(promise),
      } as unknown as DurableObjectState;
      const schemaStatements = () => statements.filter((query) => /^(DROP|CREATE) TABLE/.test(query)).length;
      const reporter = new ActivityReporter(counting, env);
      const session = { serverId: host.serverId, visibility: "public", expiresAt: nowSeconds() + 90, players: 3 };
      for (const visibility of ["public", "public", "invite", "invite"]) {
        reporter.record({ ...session, visibility } as never);
        await reporter.settled();
        scenario.clock.advance(1);
      }
      expect(schemaStatements()).toBe(2);
      await state.storage.deleteAll();
      reporter.wiped();
      reporter.record({ ...session, expiresAt: nowSeconds() + 90 } as never);
      await reporter.settled();
      expect(schemaStatements()).toBe(4);
      expect(sql.exec("SELECT players FROM public_activity_outbox_v2").one()["players"]).toBe(3);
    });
  });
  it("records a host that registers again after its object wiped its storage", async () => {
    const host = await identity(`activity-after-wipe-${scenario.day}`);
    const stub = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    const index = parseInt(host.serverId[0]!, 16);
    await register(host, scenario.ip);
    await settle(stub);
    // The first alarm ends the expired session and queues its withdrawal; the next one, once that is done, wipes.
    scenario.clock.advance(121);
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    scenario.clock.advance(91);
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    const tables = await runInDurableObject(stub, (_instance, state) =>
      state.storage.sql
        .exec<{ name: string }>("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'public_activity_outbox_v2'")
        .toArray(),
    );
    expect(tables).toEqual([]);
    expect((await shard(index).totals(nowSeconds())).sessions).toBe(0);
    await register(host, scenario.ip);
    await settle(stub);
    expect(await shard(index).totals(nowSeconds())).toMatchObject({ sessions: 1 });
  });
});
async function settle(stub: DurableObjectStub): Promise<void> {
  await runInDurableObject(stub, async (instance) => {
    await (instance as unknown as { activity: ActivityReporter }).activity.settled();
  });
}
function assertNoPrematureSnapshot(
  snapshot: { sampledAt: number } | null,
  latestPrematureSlot: number,
): void {
  // An older already-released warmup row is permitted, but the delayed
  // collection must remain private until 900 seconds after completion.
  expect(snapshot === null || snapshot.sampledAt < latestPrematureSlot).toBe(
    true,
  );
}

async function collectWithShardTotals(totals: () => unknown): Promise<void> {
  await runInDurableObject(snapshots(), async (instance) => {
    const holder = instance as unknown as { env: typeof env };
    const original = holder.env;
    holder.env = {
      ...original,
      ACTIVITY_SHARDS: {
        idFromName() { return "test-only-shard"; },
        get() { return { totals }; },
      },
    } as unknown as typeof env;
    try {
      await instance.collect();
    } finally {
      holder.env = original;
    }
  });
}
