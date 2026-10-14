/**
 * Deterministic stand-ins for the service's runtime seams (src/shared/runtime.ts). The Worker and its Durable Objects
 * run in the test isolate, so replacing these objects' members changes what production code sees.
 */
import { clock, entropy, upstream } from "../../src/shared/runtime";

const realNow = clock.now;
const realBytes = entropy.bytes;
const realFetch = upstream.fetch;

/** 2030-01-01T00:00:00Z: far enough ahead that alarms scheduled in test time never fire on their own. */
export const TEST_EPOCH_MS = Date.UTC(2030, 0, 1);

export interface TestClock {
  readonly now: () => number;
  set(milliseconds: number): void;
  advance(seconds: number): void;
}

export function useClock(start: number = TEST_EPOCH_MS): TestClock {
  let current = start;
  clock.now = () => current;
  return {
    now: () => current,
    set(milliseconds) {
      current = milliseconds;
    },
    advance(seconds) {
      current += seconds * 1000;
    },
  };
}

/** A seeded, synchronous generator (mulberry32 over a string hash): repeatable bytes, not secure ones. */
export function seededBytes(seed: string): (length: number) => Uint8Array {
  let state = 0x811c9dc5;
  for (let index = 0; index < seed.length; index++) state = Math.imul(state ^ seed.charCodeAt(index), 0x01000193) >>> 0;
  const next = (): number => {
    state = (state + 0x6d2b79f5) >>> 0;
    let value = state;
    value = Math.imul(value ^ (value >>> 15), value | 1);
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61);
    return ((value ^ (value >>> 14)) >>> 0) & 0xff;
  };
  return (length) => {
    const bytes = new Uint8Array(length);
    for (let index = 0; index < length; index++) bytes[index] = next();
    return bytes;
  };
}

export function useEntropy(seed: string): void {
  const generate = seededBytes(seed);
  entropy.bytes = (length) => generate(length) as Uint8Array<ArrayBuffer>;
}

/** Restores real randomness while keeping a test's clock and upstream. */
export function useRealEntropy(): void {
  entropy.bytes = realBytes;
}

export interface FakeUpstream {
  readonly requests: Request[];
  respond(handler: (request: Request) => Response | Promise<Response>): void;
}

/** Answers outbound provider calls; anything not handled fails loudly. */
export function useUpstream(handler?: (request: Request) => Response | Promise<Response>): FakeUpstream {
  const requests: Request[] = [];
  let current = handler ?? (() => new Response("no fake upstream answer", { status: 599 }));
  upstream.fetch = async (request) => {
    requests.push(request.clone());
    return current(request);
  };
  return {
    requests,
    respond(next) {
      current = next;
    },
  };
}

export function restoreRuntime(): void {
  clock.now = realNow;
  entropy.bytes = realBytes;
  upstream.fetch = realFetch;
}
