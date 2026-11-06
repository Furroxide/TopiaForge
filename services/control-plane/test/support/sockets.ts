/**
 * A signaling client for tests. Frames are queued as they arrive and awaited as events; the timeout only stops a
 * broken test from hanging, and no test waits on the wall clock for anything to happen.
 */
import { SELF } from "cloudflare:test";
import { stringify, type JsonObject } from "../../src/shared/canonical-json";
import { ORIGIN } from "./api";

const EVENT_TIMEOUT_MS = 5000;

export interface Closure {
  readonly code: number;
  readonly reason: string;
}

export class SignalClient {
  private readonly frames: JsonObject[] = [];
  private waiting: ((frame: JsonObject) => void) | null = null;
  private closure: Closure | null = null;
  private closeWaiters: ((closure: Closure) => void)[] = [];

  constructor(readonly socket: WebSocket) {
    socket.accept();
    socket.addEventListener("message", (event) => {
      const frame = JSON.parse(event.data as string) as JsonObject;
      if (this.waiting !== null) {
        const resolve = this.waiting;
        this.waiting = null;
        resolve(frame);
      } else {
        this.frames.push(frame);
      }
    });
    socket.addEventListener("close", (event) => {
      this.closure = { code: event.code, reason: event.reason };
      for (const resolve of this.closeWaiters) resolve(this.closure);
      this.closeWaiters = [];
    });
  }

  send(frame: JsonObject | string | ArrayBuffer): void {
    this.socket.send(typeof frame === "string" || frame instanceof ArrayBuffer ? frame : stringify(frame));
  }

  /** The next frame this client receives. */
  next(): Promise<JsonObject> {
    const queued = this.frames.shift();
    if (queued !== undefined) return Promise.resolve(queued);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("no frame arrived")), EVENT_TIMEOUT_MS);
      this.waiting = (frame) => {
        clearTimeout(timer);
        resolve(frame);
      };
    });
  }

  /** Resolves when the service closes this socket. */
  closed(): Promise<Closure> {
    if (this.closure !== null) return Promise.resolve(this.closure);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("the socket stayed open")), EVENT_TIMEOUT_MS);
      this.closeWaiters.push((closure) => {
        clearTimeout(timer);
        resolve(closure);
      });
    });
  }

  /** Frames already received and not yet read. */
  pending(): readonly JsonObject[] {
    return this.frames;
  }

  close(): void {
    try {
      this.socket.close(1000, "done");
    } catch {
      // Already closed.
    }
  }
}

export type Upgrade = { readonly client: SignalClient; readonly status: 101 } | { readonly client: null; readonly status: number; readonly body: string };

export async function openSignal(serverId: string, credential: string | null, ip: string, headers: Readonly<Record<string, string>> = {}): Promise<Upgrade> {
  const request = new Headers({ upgrade: "websocket", "cf-connecting-ip": ip, ...headers });
  if (credential !== null) request.set("authorization", `Bearer ${credential}`);
  const response = await SELF.fetch(`${ORIGIN}/v1/signal/${serverId}`, { headers: request });
  if (response.status !== 101 || response.webSocket === null) return { client: null, status: response.status, body: await response.text() };
  return { client: new SignalClient(response.webSocket), status: 101 };
}

export async function connect(serverId: string, credential: string, ip: string): Promise<SignalClient> {
  const upgrade = await openSignal(serverId, credential, ip);
  if (upgrade.client === null) throw new Error(`upgrade refused: ${upgrade.status} ${upgrade.body}`);
  return upgrade.client;
}
