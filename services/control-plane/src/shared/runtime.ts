/**
 * The service's three seams to the outside world: time, randomness and outbound HTTP. Production code reads them
 * only through these objects, so tests can run time forward, replay seeded randomness and stand in for the relay
 * provider without patching globals. Nothing in production replaces them.
 */

/** Wall-clock time in milliseconds since the Unix epoch. */
export const clock = {
  now: (): number => Date.now(),
};

/** Cryptographically secure random bytes. */
export const entropy = {
  bytes(length: number): Uint8Array<ArrayBuffer> {
    const bytes = new Uint8Array(length);
    crypto.getRandomValues(bytes);
    return bytes;
  },
};

/** Outbound HTTP to the relay provider and its usage analytics. */
export const upstream = {
  fetch: (request: Request): Promise<Response> => fetch(request),
};

export function nowSeconds(): number {
  return Math.floor(clock.now() / 1000);
}
