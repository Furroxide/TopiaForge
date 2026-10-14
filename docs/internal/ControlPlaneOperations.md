# Multiplayer control plane operations

Status: Ready for first deploy (WS7 of the live-multiplayer plan) · Date: 2026-10-14 · Owner: maintainer

This is the runbook for the control plane in `services/control-plane/`: what to create before the first deploy, how
to deploy and update it, how to rotate its secrets, how to use and recover from the kill switch, how to watch relay
spend, and what the service keeps and for how long. The API is in [ControlPlaneApi.md](ControlPlaneApi.md).

**This is the only document that carries the control plane's budget figures.** Keep spend ceilings and money out of
the API document, the code and every player-facing page. The relay pricing facts themselves are recorded once, in
[MultiplayerTransportOptions.md](MultiplayerTransportOptions.md), section 1.

## 1. What runs where

| Piece | Where |
| --- | --- |
| Worker `topiaforge-control-plane-staging` | Cloudflare, deployed from `dev` or `main` |
| Worker `topiaforge-control-plane-production` | Cloudflare, deployed from `main` only |
| Durable Objects `HostSession`, `DirectoryShard`, `InviteShard`, `SpendGuard`, `RateLimiter` | Inside each Worker, SQLite-backed, created by migration `v1` on the first deploy |
| Hourly cron (minute 7) | Each Worker; measures relay usage and can trip the kill switch |
| Relay | Cloudflare Realtime TURN, one key per environment |
| Deploy | `.github/workflows/control-plane.yml`, manual dispatch through a protected GitHub environment |

`wrangler.toml` holds no account details. The deploy reads the account from the environment, and every Worker
secret is set by hand, out of band.

## 2. Create these first

Do the steps in order: the deploy workflow refuses to run until the GitHub environment exists and is protected.

### 2.1 Cloudflare

1. **Plan.** Use the Workers Paid plan for production. The service also deploys on the free plan, but its request
   and Durable Object allowances are too small for a public browser. Check Cloudflare's current pricing page; at the
   volumes this service expects, usage stays inside the paid plan's included allowances.
2. **Deploy token.** Create an API token from the "Edit Cloudflare Workers" template, limited to this one account
   (and to the zone, if you route the Worker through a custom domain). It is used only by the deploy workflow.
3. **TURN keys.** In the dashboard, open Realtime, then TURN, and create one key for staging and one for production.
   Each gives a key id and an API token; keep them for section 2.3.
4. **Analytics token.** Create an API token with Account Analytics read only. The hourly measurement uses it to read
   the `callsTurnUsageAdaptiveGroups` dataset. One token can serve both environments.
5. **Origin.** The first deploy publishes each Worker on its `workers.dev` address. To use a custom domain instead,
   add a `routes = [{ pattern = "<host>", custom_domain = true }]` line under that environment in `wrangler.toml`,
   in a reviewed pull request.

### 2.2 GitHub environments

Create two environments under repository Settings, Environments, before any deploy:

| Setting | `control-plane-staging` | `control-plane-production` |
| --- | --- | --- |
| Required reviewers | The maintainer | The maintainer |
| Prevent self-review | Off, while there is one maintainer | Off, while there is one maintainer |
| Allow administrators to bypass | Off | Off |
| Deployment branches | Selected: `dev` and `main` | Selected: `main` |
| Secret `CLOUDFLARE_API_TOKEN` | The deploy token | The deploy token |
| Secret `CLOUDFLARE_ACCOUNT_ID` | The account id | The account id |
| Variable `CONTROL_PLANE_ORIGIN` | The staging origin, `https://…` | The production origin, `https://…` |

The workflow's preflight job reads the environment before any job names it, and stops unless it exists with a
required reviewer, no administrator bypass and custom branch rules. That replaces the governance rule that an
environment must exist before a workflow references it: a dispatch can never create an unprotected environment.

Once both environments exist, register them in the governance desired state, so the live audit watches them:

- add to `environments` in `.github/repository-governance.json`:

  ```json
  {
    "name": "control-plane-staging",
    "can_admins_bypass": false,
    "protected_branches": false,
    "custom_branch_policies": true,
    "reviewer_ids": [221987073],
    "prevent_self_review": false,
    "branch_policies": [
      {"name": "dev", "type": "branch"},
      {"name": "main", "type": "branch"}
    ]
  },
  {
    "name": "control-plane-production",
    "can_admins_bypass": false,
    "protected_branches": false,
    "custom_branch_policies": true,
    "reviewer_ids": [221987073],
    "prevent_self_review": false,
    "branch_policies": [
      {"name": "main", "type": "branch"}
    ]
  }
  ```

- add both names to the expected set in `test_only_active_privileged_environments_are_desired` in
  `.github/scripts/test_audit_repository_governance.py`;
- add both rows to the environment table in [RepositoryGovernance.md](../RepositoryGovernance.md);
- run `python3 .github/scripts/audit_repository_governance.py check`.

They are deliberately not registered before they exist: the release workflow runs the live audit, and an
environment that is desired but missing would block a release.

### 2.3 Worker secrets

Set these for each environment with `npx wrangler secret put <NAME> --env staging` (or `--env production`) from
`services/control-plane`, signed in with `npx wrangler login` or a token in `CLOUDFLARE_API_TOKEN`. Each command
prompts for the value, so it never lands in shell history. The first deploy must exist before secrets can be set
(section 3).

| Secret | Value | Without it |
| --- | --- | --- |
| `SERVICE_KEY` | 32 random bytes as unpadded base64url: `openssl rand -base64 32 \| tr '+/' '-_' \| tr -d '='` | Every keyed feature answers `service-unavailable` |
| `ADMIN_TOKEN` | At least 32 random characters: `openssl rand -base64 48 \| tr '+/' '-_' \| tr -d '='` | Admin answers `401` |
| `TURN_KEY_ID` | The environment's TURN key id | Relay is unavailable |
| `TURN_KEY_API_TOKEN` | That key's API token | Relay is unavailable |
| `ANALYTICS_ACCOUNT_ID` | The account id (32 hexadecimal characters) | The hourly measurement fails (section 5.2) |
| `ANALYTICS_API_TOKEN` | The analytics token | The hourly measurement fails |
| `TURN_DAILY_CAP_GB` | Decimal gigabytes (section 6.2) | Relay is unavailable |
| `TURN_TICKET_CAP_GB` | Decimal gigabytes (section 6.2) | Relay is unavailable |

Use a different `SERVICE_KEY` and `ADMIN_TOKEN` for each environment. The spend ceilings are secrets only so that
they are set by hand and never sit in the repository; they are not sensitive in themselves.

## 3. First deploy

1. Merge the service to `dev`. Before the first dispatch, check `RELAY_ADDRESS_RANGES` against Cloudflare's current
   Realtime TURN documentation and published ranges, and correct it by pull request (section 4.1). The shipped value
   has never been checked.
2. In Actions, run **Control plane** with `control-plane-staging` on `dev`. Approve the deployment when GitHub asks.
   The job deploys the Worker and applies migration `v1`, which creates the five Durable Object classes, then
   smoke-tests `GET /v1/status`.
3. Set the eight secrets for staging (section 2.3).
4. Check the deployment:

   ```bash
   origin=https://…                    # the staging origin
   read -rs ADMIN && export ADMIN      # paste the staging admin token
   curl -s "$origin/v1/status"
   curl -s -H "Authorization: Bearer $ADMIN" "$origin/v1/admin/state"
   curl -s -X POST -H "Authorization: Bearer $ADMIN" "$origin/v1/admin/spend/measure"
   ```

   Status shows every feature `true` and `killSwitch` `false`. Admin state shows `providerConfigured` and
   `analyticsConfigured` `true`. The measurement shows `measurementFailures` `0`; if it does not, fix the analytics
   token before the cron's third failure trips the kill switch (section 5.2).
5. Play an Internet session through staging: invite join, relay join with "hide my IP", and a hole-punched join.
6. Register both GitHub environments in governance (section 2.2).
7. For production, repeat steps 2 to 4 with `control-plane-production` on `main` after the release merge, and give
   the release builds the production origin (`--dart-define=TOPIAFORGE_CONTROL_PLANE_ORIGIN=…`).

## 4. Updates

- **Code.** Merge to `dev` and dispatch staging; after a release reaches `main`, dispatch production. A deploy is
  atomic, and Durable Objects pick up new code as they restart.
- **Rollback.** `npx wrangler rollback --env <environment>` restores the previous code version. It does not undo
  storage, so storage changes must stay readable by the previous version.
- **Durable Object changes.** Migrations are append-only. A new class gets a new `[[migrations]]` entry with the next
  tag (`v2`) and its class under `new_sqlite_classes`. Never rename or delete a class without its own migration step,
  and never edit or remove an applied migration.
- **Vars.** Mint limits, the credential lifetime and the relay address ranges (4.1) are vars in `wrangler.toml`,
  changed by pull request. Keep the top level, `env.staging` and `env.production` identical apart from `ENVIRONMENT`; a
  test enforces it.
- **Dependencies.** Dependabot proposes npm updates for `services/control-plane` weekly, at least 14 days after
  release. Wrangler and `@cloudflare/vitest-plugin` must move together: the plugin depends on one exact Wrangler
  release, and the service pins that same release so only one copy installs. The `sharp` override pins a patched
  release over the vulnerable one Miniflare names; remove it once Miniflare names a patched release itself.

### 4.1 Relay address ranges

`RELAY_ADDRESS_RANGES` is meant to list the ranges Cloudflare Realtime TURN allocates relay addresses from, as CIDR
blocks separated by single spaces. A relay-only side's signaling candidates are forwarded only when they lie inside it
(ControlPlaneApi.md, section 8.3). That makes the list a privacy control: a block too wide lets more through, and a
missing block breaks relay-only joins.

**The shipped value is unchecked.** It is Cloudflare's published CDN address list, IPv4 then IPv6, as written down from
memory when the service was built. Nobody has compared it with https://www.cloudflare.com/ips/, and nobody has
confirmed that Realtime TURN allocates relay addresses from those ranges at all. It is also far wider than relay needs:
it spans every address Cloudflare publishes for its network, so a candidate anywhere inside it passes as a relay
address.

Before deploying, check the list against Cloudflare's current Realtime TURN documentation and its published ranges.
Check it again every quarter, and whenever Cloudflare announces a change of its addresses:

1. Find the ranges Realtime TURN allocates relay addresses from in Cloudflare's current documentation, and compare
   them with the published lists. If the documentation names no ranges of its own, fall back to the published lists
   at https://www.cloudflare.com/ips/, checked entry by entry, knowing that they are wider than relay needs.
2. Write the narrowest list that covers every relay range, IPv4 blocks first, joined by single spaces.
3. Replace the value in all three places in `wrangler.toml`: the top level, `env.staging` and `env.production`.
4. Run `npm test` in `services/control-plane`. The configuration tests refuse a list that does not parse in full, and
   one that differs between environments. They also pin the shipped list's size and a few addresses inside it, and
   the signaling tests and the recorded client fixtures use relay addresses in 141.101.64.0/18, 162.158.0.0/15 and
   2a06:98c0::/29. If the new list leaves those out, move the tests to addresses inside it and regenerate the fixtures
   (ControlPlaneApi.md, section 11).
5. Merge by pull request, and deploy as above.

A list that does not parse leaves relay unavailable rather than unchecked: `GET /v1/status` reports `features.relay`
`false`, and admin state reports `providerConfigured` `false`.

The relay join with "hide my IP" in the first staging session (section 3, step 5) catches a missing block: if that
joiner receives a `candidates-withheld` notice for its relay candidates, Cloudflare's relay addresses lie outside the
list. It cannot catch a list that is too wide. A wider list passes the same join without any sign, so only the check
against Cloudflare's documentation keeps the list narrow.

## 5. Rotating secrets

Rotate on suspicion at once, and otherwise yearly. Set the new value with `wrangler secret put`; it takes effect in a
few seconds without a deploy.

| Secret | Effect of rotating |
| --- | --- |
| `SERVICE_KEY` | Outstanding challenges (two minutes) and join tickets (six hours) stop working; clients request new ones. Host session tokens are unaffected. Rate-limit counters restart. |
| `ADMIN_TOKEN` | The old token stops at once. |
| `TURN_KEY_ID`, `TURN_KEY_API_TOKEN` | Create a new TURN key, set both secrets, then delete the old key in the dashboard; credentials minted under it stop working. |
| `ANALYTICS_API_TOKEN` | Set the new token, run `POST /v1/admin/spend/measure` to confirm, then revoke the old one. |
| `CLOUDFLARE_API_TOKEN` (GitHub) | Create a new deploy token, update both environments' secrets, then revoke the old token. |

### 5.1 The kill switch

The kill switch halts all relay minting at once and revokes every live credential. That includes a credential whose
mint is still waiting on the provider when the switch engages: the mint answers `relay-unavailable`, and the credential
is revoked without reaching the client. `GET /v1/status` then reports `killSwitch` `true`, and clients stop offering
relay.

Revocation reaches every credential promptly, but not in one step:

- It runs in passes of at most 40 calls to the provider, six at a time. A pass therefore stays inside the smallest
  number of subrequests Workers allow one invocation, 50.
- The request that engages the switch, or the hourly run that trips it, runs the first pass itself. Each further pass
  starts as soon as the one before it ends.
- A pass in which the provider confirms nothing is tried again a minute later.
- A credential not yet revoked keeps working until it is, or until it expires: at most `TURN_CREDENTIAL_TTL_SECONDS`,
  ten minutes by default.

With the production figures (section 6.2) at most about 1,500 credentials are live at once, which is about 40 passes
back to back. The first 40 are revoked within the engaging request; how quickly the rest follow depends on how fast the
provider answers, each call having a five-second deadline.

```bash
curl -s -X PUT -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" \
  --data '{"engaged":true,"note":"manual stop"}' "$origin/v1/admin/kill-switch"
curl -s -X PUT -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" \
  --data '{"engaged":false,"note":null}' "$origin/v1/admin/kill-switch"
```

Bodies must be canonical JSON: keys in order, no spaces. Releasing does not restore revoked credentials; clients mint
new ones.

### 5.2 Automatic trips

The hourly cron trips the kill switch itself, with a reason in admin state:

| Reason | Cause |
| --- | --- |
| `spend-cap` | Today's measured relay egress reached `TURN_DAILY_CAP_GB` |
| `measurement-failed` | `TURN_MEASUREMENT_FAILURE_LIMIT` (3) hourly measurements in a row failed |

An automatic trip stays engaged until you release it: nothing re-enables relay on its own. Failing closed on a failed
measurement is deliberate, because the relay has no spend cap of its own. Setting `TURN_MEASUREMENT_FAILURE_LIMIT` to
`0` disables that trip; do so only briefly, while fixing the analytics token.

## 6. Spend

### 6.1 How the guard counts

Each credential reserves its worst case under the transport budget of 256 kbit/s (32 KiB/s) per connection:
`credential lifetime × peers × 32 KiB/s`. A joiner relays to one peer, the host to every other player. With the
default ten-minute lifetime:

| Credential | Reserved |
| --- | --- |
| A joiner | about 19.7 MB |
| A full 16-player host, relay-only | about 295 MB (15 peers) |

A mint is refused when it would take the ticket past `TURN_TICKET_CAP_GB` or `TURN_TICKET_MAX_MINTS` (36), when the
caller's prefix has minted five in the last minute or twenty in the last hour, or when today's measured egress plus
every reservation the last measurement cannot have seen would pass `TURN_DAILY_CAP_GB`. The hourly measurement
replaces reservations with real usage once credentials have expired, so the daily gate tightens only while relayed
traffic is unmeasured. A credential whose measured egress passes twice its reservation is revoked and its ticket
blocked.

### 6.2 Starting figures

| Setting | Staging | Production | Why |
| --- | --- | --- | --- |
| `TURN_DAILY_CAP_GB` | 2 | 30 | 30 GB a day is 900 GB in a 30-day month, inside the 1,000 GB monthly free allowance |
| `TURN_TICKET_CAP_GB` | 1 | 6 | About 20 credentials, three hours and twenty minutes, for a full relay-only host at the ceiling rate |
| `TURN_CREDENTIAL_TTL_SECONDS` | 600 | 600 | The kill switch revokes at once, so the lifetime bounds only abuse between hourly measurements |

At $0.05 per GB past the free allowance, the production cap bounds relay spend at $1.50 a day if the allowance is
already used up, and about $45 in a 30-day month. Real sessions sit well below the 256 kbit/s ceiling, so measured
usage is usually a small fraction of what is reserved. Raise the caps as measured volume grows, and revisit
self-hosting only on the terms in MultiplayerTransportOptions.md, section 6.

### 6.3 Watching it

- `GET /v1/admin/state` reports today's mints, reserved and outstanding bytes, measured bytes and when they were
  measured, consecutive measurement failures, live credentials and pending revocations.
- The Cloudflare dashboard's Realtime TURN analytics show egress by key and by credential username.
- Check both weekly, and after any release that changes relay behaviour.

### 6.4 When spend trips the kill switch

1. Read admin state: the reason, the measured bytes and the outstanding reservations.
2. In the TURN analytics, look for a few credential usernames carrying most of the egress. One credential far past its
   reservation is abuse; many ordinary ones are growth.
3. For abuse: the guard has already revoked over-plan credentials and blocked their tickets. Delist the server
   involved with `POST /v1/admin/servers/{serverId}/delist` and a hold, and consider lowering
   `TURN_IP_MINTS_PER_HOUR` or `TURN_CREDENTIAL_TTL_SECONDS` by pull request. The service has no addresses to block
   by design.
4. For growth: raise `TURN_DAILY_CAP_GB` with the cost in 6.2 in mind, or leave relay off until the next UTC day.
5. Release the kill switch (5.1), and confirm `GET /v1/status` reports relay again.

## 7. Data retention

The service stores no raw IP address anywhere. Rate limits key on an HMAC of the caller's /24 or /56 prefix and the
hour. Signaling is held in memory only. Invocation logs are off in `wrangler.toml`; do not turn on Workers Logs,
Logpush or saved tail output for these Workers, because their request metadata includes the caller's address.
`wrangler tail` shows that metadata live: use it only during an incident and never save its output.

| Data | Where | Kept |
| --- | --- | --- |
| Host session: signed descriptor, session token hash, random session epoch, listing fields, player count, privacy choice | `HostSession` | Until 90 seconds after the last heartbeat; the alarm then deletes it and its invites |
| A session's invites | `HostSession`, `InviteShard` | Until expiry (at most seven days) or the session's end |
| Used challenge nonces | `HostSession` | Two minutes |
| Operator holds | `HostSession` | Up to 30 days, as set |
| Peer ids the host said goodbye to, derived from random ticket ids | `HostSession` | Until that peer's ticket expires, or, for a peer already gone at the goodbye, six hours after it; at most six hours, and never past the session's end |
| Public listings | `DirectoryShard` | Until the session's expiry |
| Rate-limit counts, keyed by prefix hash and hour | `RateLimiter`, `SpendGuard` | At most two hours |
| Relay accounting per ticket (random ticket id or token hash) | `SpendGuard` | A day past the ticket's expiry |
| Minted credential usernames | `SpendGuard` | Two hours past expiry |
| Daily spend totals | `SpendGuard` | 35 days |
| Feature flags, kill switch, latest measurement | `SpendGuard` | Until changed |
| Signaling frames | Memory only | Never stored |

Cloudflare itself sees callers' addresses to serve traffic, as any host does; that processing is outside what this
service stores, and the player-facing privacy text (P0-PRIV-01 in [PrivacyAndCapabilities.md](../PrivacyAndCapabilities.md))
must say so before Internet play is enabled.

## 8. Troubleshooting

| Symptom | Check |
| --- | --- |
| The deploy's preflight fails on the environment check | Compare `gh api repos/Furroxide/TopiaForge/environments/<name>` with the `jq` expression in the workflow: the environment must exist, have a required reviewer, refuse administrator bypass and use custom branch rules. The expression holds for the `release` environment as configured today. |
| The deploy's smoke test fails | `CONTROL_PLANE_ORIGIN` must be set on the environment as `https://` plus a host, with no path or trailing slash. |
| Everything answers `503 service-unavailable` | `SERVICE_KEY` is missing or is not base64url of 32 to 64 bytes. |
| `features.relay` is `false` with the kill switch released | A TURN secret, a spend ceiling or `RELAY_ADDRESS_RANGES` is missing or malformed; admin state shows `providerConfigured` `false`. |
| Relay-only joins fail while other relay joins work | The joiner receives a `candidates-withheld` notice for its relay candidates, so Cloudflare's relay addresses lie outside `RELAY_ADDRESS_RANGES`. Compare the list with Cloudflare's (section 4.1). |
| `measurementFailures` climbs | The analytics token lacks Account Analytics read, `ANALYTICS_ACCOUNT_ID` is wrong, or the dataset query changed; run `POST /v1/admin/spend/measure` after each fix. The query is in `src/relay/usage-api.ts`. |
| Admin answers `429` | Admin requests are limited per prefix (30 a minute); wait a minute. |
