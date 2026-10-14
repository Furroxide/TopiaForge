import { runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { clock } from "../src/shared/runtime";
import { call, identity, register } from "./support/api";
import { TEST_EPOCH_MS, restoreRuntime, useClock, useEntropy } from "./support/runtime";

describe("the test harness", () => {
  beforeEach(() => {
    useClock();
    useEntropy("smoke");
  });
  afterEach(() => restoreRuntime());

  it("serves the status document", async () => {
    const result = await call("GET", "/v1/status", { ip: "198.51.100.1" });
    expect(result.status).toBe(200);
    expect(result.json["schema"]).toBe("topiaforge.control-plane-status/1");
  });

  it("registers a host, and its session object sees the test clock", async () => {
    const host = await identity("smoke-host");
    const registered = await register(host, "198.51.100.2");
    expect(registered.response.json["serverId"]).toBe(host.serverId);
    const stub = env.HOST_SESSIONS.get(env.HOST_SESSIONS.idFromName(host.serverId));
    expect(await runInDurableObject(stub, () => clock.now())).toBe(TEST_EPOCH_MS);
  });
});
