/**
 * Per-test isolation inside one test file, whose storage the pool shares between its tests. Each test runs on its own
 * simulated day, so every hourly rate-limit key, window and daily budget is fresh; identities are derived from the
 * test's label; and the relay provider is a fresh fake.
 */
import { clearFlagCache } from "../../src/api/context";
import { FakeRelayProvider } from "./relay";
import { TEST_EPOCH_MS, restoreRuntime, useClock, useEntropy, useUpstream, type TestClock } from "./runtime";

let sequence = 0;

export interface Scenario {
  readonly clock: TestClock;
  readonly relay: FakeRelayProvider;
  readonly day: number;
  /** A documentation address unique to this test. */
  readonly ip: string;
  /** Another address in a different /24, for a second caller. */
  readonly otherIp: string;
}

export function startScenario(seed: string): Scenario {
  sequence++;
  clearFlagCache();
  const clock = useClock(TEST_EPOCH_MS + sequence * 24 * 60 * 60 * 1000);
  useEntropy(`${seed} ${sequence}`);
  const relay = new FakeRelayProvider();
  useUpstream(relay.handle);
  return { clock, relay, day: sequence, ip: `203.0.113.${(sequence % 200) + 1}`, otherIp: `198.51.100.${(sequence % 200) + 1}` };
}

export function endScenario(): void {
  restoreRuntime();
  clearFlagCache();
}
