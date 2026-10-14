/** A stand-in for Cloudflare Realtime TURN and its usage analytics, answering the requests the service sends. */
export const TURN_ORIGIN = "https://rtc.live.cloudflare.com";
export const GRAPHQL_URL = "https://api.cloudflare.com/client/v4/graphql";

export class FakeRelayProvider {
  minted = 0;
  readonly revoked: string[] = [];
  readonly generateBodies: unknown[] = [];
  readonly authorizations: string[] = [];
  egressBytes = 0;
  readonly byUsername = new Map<string, number>();
  failGenerate = false;
  failUsage = false;
  failRevoke = false;
  /** When set, mints wait on this promise, so tests can hold requests in flight. */
  gate: Promise<void> | null = null;
  private arrivalWaiters: { readonly count: number; readonly resolve: () => void }[] = [];

  /** Resolves once `count` mint requests have reached the provider. */
  mintsArrived(count: number): Promise<void> {
    if (this.generateBodies.length >= count) return Promise.resolve();
    return new Promise((resolve) => this.arrivalWaiters.push({ count, resolve }));
  }

  readonly handle = async (request: Request): Promise<Response> => {
    const url = new URL(request.url);
    this.authorizations.push(request.headers.get("authorization") ?? "");
    if (url.origin === TURN_ORIGIN && url.pathname.endsWith("/credentials/generate-ice-servers") && request.method === "POST") {
      this.generateBodies.push(await request.json());
      for (const waiter of this.arrivalWaiters.filter((entry) => this.generateBodies.length >= entry.count)) waiter.resolve();
      this.arrivalWaiters = this.arrivalWaiters.filter((entry) => this.generateBodies.length < entry.count);
      if (this.gate !== null) await this.gate;
      if (this.failGenerate) return new Response("upstream failure", { status: 500 });
      this.minted++;
      return Response.json(
        {
          iceServers: [
            { urls: ["stun:stun.cloudflare.com:3478", "stun:stun.cloudflare.com:53"] },
            {
              urls: [
                "turn:turn.cloudflare.com:3478?transport=udp",
                "turn:turn.cloudflare.com:3478?transport=tcp",
                "turns:turn.cloudflare.com:5349?transport=tcp",
              ],
              username: `test-user-${this.minted}`,
              credential: `test-credential-${this.minted}`,
            },
          ],
        },
        { status: 201 },
      );
    }
    const revoke = /\/credentials\/([^/]+)\/revoke$/.exec(url.pathname);
    if (url.origin === TURN_ORIGIN && revoke !== null && request.method === "POST") {
      if (this.failRevoke) return new Response("upstream failure", { status: 500 });
      this.revoked.push(decodeURIComponent(revoke[1]!));
      return new Response(null, { status: 204 });
    }
    if (url.href === GRAPHQL_URL && request.method === "POST") {
      if (this.failUsage) return new Response("analytics failure", { status: 500 });
      return Response.json({
        data: {
          viewer: {
            accounts: [
              {
                total: [{ sum: { egressBytes: this.egressBytes } }],
                heaviest: [...this.byUsername].map(([username, egressBytes]) => ({ dimensions: { username }, sum: { egressBytes } })),
              },
            ],
          },
        },
        errors: null,
      });
    }
    return new Response("unexpected request", { status: 599 });
  };
}
