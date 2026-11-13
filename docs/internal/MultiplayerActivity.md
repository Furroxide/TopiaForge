# Public multiplayer activity aggregates

Activity instrumentation is additive and inactive until both `MONITOR_ACTIVITY_TOKEN` (at least 32 characters) and `MONITOR_SERVER_IDS` (whitespace-separated SHA-256 public signing-key hashes) are configured for an environment. Configure these through Worker secret settings; use a separate token from `ADMIN_TOKEN` and deployment credentials. Match the public identity to the status repository's stable synthetic signing key before enabling scheduled probes.

Successful registration, descriptor updates and heartbeats coalesce an expiring count lease in a per-host durable outbox. Ending or expiring a session publishes a tombstone. Leases are keyed internally by server ID and ordered by a monotonic host-local version, with at most three delivery attempts and a ninety-second retry lifetime. Count reads ignore leases whose existing ninety-second host lifetime expired; tombstones reject delayed older updates. Analytics failures never refuse hosting or heartbeats. Totals are host-reported estimates and can lag while delivery retries.

Sixteen activity shards include public and invite-only Internet sessions across every build and protocol. They hold no addresses, names, descriptors, candidates, invites, tokens or player identities. Configured synthetic IDs receive zero-lifetime leases and never contribute to totals. LAN-only sessions do not register with the service and therefore do not contribute.

The five-minute cron collects all sixteen shards. A partial read becomes `unavailable`, never a partial sum. The snapshot stores null counts when fewer than five sessions or ten players are active. Suppression happens before snapshot storage. Reads additionally wait fifteen minutes from the actual collection time, not merely the nominal time slot. Each collection slot is immutable; warmup lasts ninety seconds. Snapshot retention is one day.

`GET /v1/monitor/activity` requires the dedicated bearer token and returns only schema `topiaforge.activity/1`, `sampledAt` (UTC seconds), `state` (`available`, `suppressed`, `unavailable`), `sessions`, and `players`. Both counts are null for suppressed or unavailable data. Until a delayed snapshot exists it returns 503. Wrong or absent credentials uniformly return 401. Neither public browsing nor `/v1/status` changes its response contract. The status page must never expose this credential.

One `*/5 * * * *` cron runs both activity collection and the relay-spend measurement. Durable Object migration `v2` appends `ActivityShard` and `ActivitySnapshot`; migration `v1` is unchanged. Invocation logs and preview domains remain disabled.

Deploy staging through the existing protected workflow and verify a real released snapshot before enabling production. Production activation remains part of the production service release. The page, runner, source-reviewed incident notes and commissioning instructions live in `Furroxide/TopiaForge-Status`.

## Author and player experience

No launcher or game change is required. A service deployment observes the counts already supplied by hosts; public analytics preserve small-session privacy and never enumerate invite-only sessions. Synthetic probes use ordinary service limits, with two TURN credentials every ten minutes rather than increasing the twenty-per-hour IP limit.
