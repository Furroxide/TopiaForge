# TopiaForge Developer Docs — Decision Document

Status: Proposed · Date: 2026-06-29 · Owner: docs/platform · Repo: repository root

---

## 1. TL;DR / Recommendation

- **Site generator:** **Astro Starlight** for the hand-written portal (Tutorials, How-to, Explanation, manifest tables). Built-in Pagefind search, lighter/faster than Docusaurus, one fewer thing to wire.
- **C# API reference:** **DocFX** over `TopiaForge.Mods.Abstractions`, output deployed at `/api/csharp/` — *gated on adding `///` comments first*.
- **Dart API reference:** **dartdoc** (`dart doc`) per `launcher_*` package, output at `/api/dart/`, **strongly de-emphasized** (Contributing/Internals only — these packages are internal, and the audience is C# modders). *Decision point: this tree may not earn its place in the unified search index — see §3.3.*
- **Hosting + CI:** **GitHub Pages** via a **GitHub Actions** workflow (build DocFX + dartdoc + Starlight on a runner, stitch into one publish dir). Single vendor, $0 on public repos.
- **Search:** **Pagefind** — Starlight ships it for the portal; one extra post-build pass over the *merged* HTML extends it across portal + C# API (+ Dart API, if kept).
- **Content framework:** task-oriented IA, led by a Quickstart, with Diátaxis used only as a loose mental check (Tutorial / How-to / Reference / Explanation) — not as a rigid four-folder taxonomy a ~18-page site doesn't need.
- **Versioning:** deferred — Starlight's `starlight-versions` plugin only when a breaking SDK change actually lands; start with `latest` only.

**Why this and not the alternatives:** Starlight gives the lightest credible portal with **search built in**, which matters more than Docusaurus's built-in versioning for a team that is explicitly deferring versioning indefinitely. DocFX + dartdoc are the native, free, Windows-friendly generators for each language, and Pagefind is the only easy way to unify search across separate generators' HTML. We pass on Docusaurus (heavier, justified mainly by versioning we're deferring — kept as the fallback if we commit to versioning), Material for MkDocs (frozen Nov 2025; successor Zensical too new), Sphinx/RTD (Python autodoc useless for C#/Dart), and GitBook (SaaS lock-in).

**Two genuine forks for you to decide** (details in §8):
1. **Commit to versioning now?** If per-game/per-loader version docs are needed in the near term, switch the portal to **Docusaurus 3** (mature built-in versioning) instead of Starlight. Otherwise Starlight stands.
2. **Collapse to one or two toolchains?** If minimizing toolchains beats best-of-breed, either make **DocFX the whole site** (conceptual Markdown + C# API, .NET-only, dartdoc hosted alongside) or use **Writerside** (single tool: guides + API reference + search). Both trade polish/flexibility for fewer moving parts.

---

## 2. Goals & Audiences

**Goals (in priority order):**
1. Serve **external C# mod developers** an end-to-end path from SDK → built `.topiaforgemod` → installed → iterating.
2. **Low long-term maintenance** for a solo/small team — auto-generate the rot-prone API surface; hand-write only stable prose; keep the toolchain count as low as the goals allow.
3. **Credibility** at least matching the legacy RoboPatch wiki's content surface (clean-room — mirror topics, never copy).
4. **$0 hosting, Windows-first authoring**, good full-text search, Markdown authoring.
5. *Nice-to-have:* per-game/per-loader-version docs.

**Audiences:**

| Priority | Audience | Core need |
|---|---|---|
| **1 (primary)** | External C# mod developers | Reference SDK → derive from `TopiaForgeMod` → manifest → `topiaforge dev` → services/modules → publish. Needs the restart-required/Mono-no-unload model explained. |
| 2 | End users (launcher) | Install/detect game, install/enable/disable mods, dependency/conflict plans, diagnostics. Mostly *user* docs; overlaps dev docs only at the package-format boundary. |
| 3 | Internal contributors | Architecture map, `AGENTS.md` rules (Bloc, 500-line Dart cap, clean-room, Core free of Unity), verification matrix, where the manifest schema *actually* lives (Dart `launcher_domain`). |

---

## 3. Options Considered (per layer)

### 3.1 Site generator (portal shell)

| Option | Pros | Cons |
|---|---|---|
| **Astro Starlight** ✅ | Built-in Pagefind search (no extra wiring for the portal); fast builds; clean credible theme; easy Tailwind branding; trivial to drop `/api/*` HTML alongside | Needs Node; versioning is the pre-1.0 `starlight-versions` plugin; younger ecosystem than Docusaurus |
| **Docusaurus 3** (fallback) | Built-in **mature** versioning; large ecosystem; credible default theme | Heaviest toolchain (React/Webpack); search needs a plugin (Pagefind/local) or Algolia; only worth it if we commit to versioning now |
| **mdBook** | Rust single static binary, **zero Node**; trivial on Windows; built-in search | Spartan theme/extensibility; weak nav for a multi-section site; no versioning; less credible "product" look |
| **Material for MkDocs** | Best Markdown ergonomics; great offline search | **Frozen 2025-11-05** (9.7.0 final, Insiders freed); maintainer pivoted to successor **Zensical** — bad multi-year bet (see §8) |

**Pick: Astro Starlight** — lightest path with search included, matching the deferred-versioning stance. Docusaurus is the fallback the moment versioning becomes a near-term requirement (Fork 1, §8). mdBook was the strongest no-Node contender but loses on theme/nav credibility for a public modding portal.

### 3.2 C# API reference generator

| Option | Pros | Cons |
|---|---|---|
| **DocFX** ✅ | Free/MIT, `dotnet tool`, Windows-native; reads `.csproj`/DLL+XML; modern template; built-in search; the moment you add `///` it upgrades in place; .NET Foundation project, still shipping (2.78.x, .NET 9) | Irregular release cadence, ~1 lead maintainer; signatures-only until `///` added |
| **Sandcastle (SHFB)** | Most actively maintained (2026 releases); authoritative MSDN look | Heavier (GUI heritage, .NET FW 4.8 dep); dated default look — overkill |
| **Doxygen** | `EXTRACT_ALL` documents uncommented code | C# is second-class; dated C/C++ look; no clean conceptual-merge |

**Pick: DocFX** (SHFB is the fallback if DocFX's cadence stalls). Output mode: generate API HTML only, mount under `/api/csharp/` — let the portal own conceptual docs.

### 3.3 Dart API reference / unification

| Option | Pros | Cons |
|---|---|---|
| **dartdoc per-package, de-emphasized** ✅ | Ships with Dart SDK (zero install); familiar pub.dev look; static HTML drops into the deploy | No multi-package mode (run per package + stitch index); subpath hosting awkward; theme won't match portal |
| **Skip Dart API tree entirely** | Removes a runtime, a CI step, and a theme-mismatch from search; matches the C#-modder audience | Internal contributors lose generated Dart reference (mitigated: source + `///` still readable in-repo) |
| **Unified single generator (DocFX + custom Dart converter)** | One theme, UID xrefs | DocFX has **no Dart support** — a custom converter is real ongoing maintenance for near-zero payoff |
| **Publish packages to pub.dev for auto-docs** | Free hosted dartdoc | Packages are `publish_to: none` (internal) — publishing leaks internals; wrong |

**Pick: dartdoc, scoped down — and openly optional.** The Dart tree serves only internal contributors, so its inclusion in the *unified search index* is a judgment call: if the theme discontinuity in cross-search results (see §8) annoys more than the reference helps, **drop it from Pagefind and link to it standalone from Contributing**. Mechanism: a plain **PowerShell loop** running `dart doc` in each of `packages/launcher_domain`, `packages/launcher_data`, `packages/launcher_ui` and copying each output to `/api/dart/<pkg>/` behind a small index. **No melos / pub workspace exists in this repo** — do not assume one. Do **not** attempt a unified C#+Dart API model — no tool does this natively.

### 3.4 Hosting + CI

| Option | Pros | Cons |
|---|---|---|
| **GitHub Pages via Actions** ✅ | One vendor (already on GitHub); free custom domain + HTTPS; Actions free on **public** repos; static-perfect | No native PR previews (add `rossjrw/pr-preview-action`); free Pages needs a public repo |
| **Cloudflare Pages** | Automatic per-PR previews; unlimited bandwidth; serves a prebuilt artifact regardless of repo visibility | Second vendor + DNS/token; CF build image not .NET-tuned (build in Actions anyway) |
| **Netlify / Vercel** | Polished previews | Tight free caps (Netlify) / non-commercial Hobby tier (Vercel) |

**Pick: GitHub Pages** (build in Actions). **Switch the deploy step to Cloudflare Pages** the moment you want free PR previews or a private docs source — it's a one-line change since the build stays in Actions.

> **CI cost footnote:** GitHub Actions remains free on **public** repos. If you ever move the docs source to a **private** repo *and* use **self-hosted** runners, note GitHub's 2026 per-minute platform charge for self-hosted runners on private repos; GitHub-hosted minutes on private repos still bill against the included quota as before.

### 3.5 Search

| Option | Pros | Cons |
|---|---|---|
| **Pagefind** ✅ | MIT/$0; **generator-agnostic** — one pass over merged HTML = single search box across portal + DocFX (+ dartdoc); ships with Starlight for the portal; lazy-loads chunks | No fuzzy/typo tolerance; no query analytics; +1 CI step for the merged index |
| **Algolia DocSearch** | Best relevance + typo tolerance + analytics; free for qualifying OSS docs | **Largely self-serve now** (automated validation, instant approval if criteria met, else 1–2 day review); still requires logo attribution; vendor dependency |
| **Lunr (DocFX built-in)** | Zero work for the DocFX section | Only indexes DocFX output — **can't unify** the hybrid site |

**Pick: Pagefind** — the only option that gives *one* search box across separate generators' output, and Starlight already uses it. Algolia DocSearch is a viable later upgrade if typo tolerance/analytics become a real complaint; its onboarding is now mostly self-serve, so the upgrade cost is low.

### 3.6 Content framework

| Option | Pros | Cons |
|---|---|---|
| **Task-oriented IA + Diátaxis as a mental check** ✅ | Fixes RoboPatch's single-page sprawl; low ceremony for ~18 pages; Quickstart-first; pushes volatile detail into generated Reference | Less prescriptive than strict Diátaxis; relies on author judgment to keep types from blending |
| **Strict Diátaxis (4 folders)** | Clear taxonomy; popular convention | Four-quadrant rigidity is overhead for a small site; confuses newcomers without a curated Quickstart anyway |
| Ad-hoc wiki (RoboPatch style) | No discipline cost | Blends tutorial+reference+how-to per page → rots fast |

**Pick: task-oriented IA**, fronted by a Quickstart, with Diátaxis as a loose routing heuristic (one-line rule in `CONTRIBUTING.md`) rather than an enforced folder structure.

---

## 4. Recommended Architecture

```
Published site (one GitHub Pages artifact, served at /)
├── /                      Starlight portal (hand-written: Tutorials, How-to, Explanation, manifest tables)
├── /api/csharp/           DocFX output of TopiaForge.Mods.Abstractions   (generated, prominent in modder nav)
├── /api/dart/             dartdoc output (generated, Contributing/Internals only — optional in search)
│   ├── launcher_domain/
│   ├── launcher_data/
│   └── launcher_ui/
└── /pagefind/             Pagefind index built AFTER the trees are merged → one search box
```

**Build pipeline (GitHub Actions, single workflow, runs identically on Linux runner and the Windows dev box):**

1. `setup-dotnet` → `dotnet restore` → `dotnet tool install -g docfx` → `docfx build` → emit C# API HTML to `build/site/api/csharp/`.
2. `setup-dart`/Flutter → **PowerShell loop** running `dart doc` over `launcher_domain`, `launcher_data`, `launcher_ui` → copy each to `build/site/api/dart/<pkg>/` → write a small `index.html`. *(Skip this step if the Dart tree is dropped — see §3.3.)*
3. `npm ci && npm run build` (Starlight) → output to `build/site/` (the `api/` trees copied in post-build).
4. **`pagefind --site build/site`** — re-indexes the *merged* HTML so portal + C# API (+ Dart) share one index. *(If the Dart tree is excluded, scope the crawl to portal + `/api/csharp/`.)*
5. Deploy: `actions/upload-pages-artifact@v3` + `actions/deploy-pages@v4`. (Swap to Wrangler→Cloudflare for PR previews.)

**Key property:** each generator owns its own tree (host-together, link-out — *not* merged into a unified model), so there's no tool-interop to maintain. The only unification is Pagefind's HTML crawl. Docs track code because the API tier is regenerated every build.

**Known UX cost:** cross-search results jump between visually distinct themes (Starlight, DocFX template, pub.dev-style dartdoc). Acceptable for portal↔C#; it's the main reason the Dart tree's place in the unified index is left as a deliberate decision (§3.3).

---

## 5. Information Architecture (sitemap)

Section type in brackets (loose Diátaxis check). **Legend:** 🟢 exists today · 🟡 partial today · ⚪ new · 🤖 auto/semi-generated.

| Page | Type | Source | Status |
|---|---|---|---|
| Home / Overview (loader vs launcher, by-audience start) | — | hand | ⚪ |
| **Getting Started (Modders)** — template → build → `topiaforge pack` → install → F10 → logs | Tutorial | hand, snippets from CI-built template | 🟡 (README + Modding.md fragments) |
| Your First Mod (full walkthrough) | Tutorial | hand + `topiaforge new mod` scaffold (`templates\mod\*`) | 🟢 (docs/YourFirstMod.md) |
| Mod Anatomy (`.topiaforgemod` layout, what `topiaforge pack` includes/strips) | Reference | hand | ⚪ |
| **Manifest Reference (`topiaforge.mod.json`)** | Reference | hand-maintained, **CI-validated** against `ModManifest`/`ModDependency`/`ModConflict` (see §7) | 🟡 (Modding.md lists most fields; verify `worldGamemodes` and dependency version-range objects) |
| Mod Lifecycle & Context (`OnLoad/OnUnload`, `IModContext`, restart-required/Mono no-unload) | Explanation+Ref | hand | 🟡 |
| Configuration (`LoadConfig/SaveConfig`, `[DataContract]`, `ModPaths`, `IModFileService`) | How-to | hand | ⚪ |
| Services Overview (`GetService<T>`, additive contract, `IModServiceRegistry`) | Reference | hand | ⚪ |
| Asset Bundle Guide (`IAssetBundleService`) | How-to | hand | ⚪ |
| Prompt Override Guide (`IPromptOverrideRegistry`, priority, `PromptConflict`) | How-to | hand | ⚪ |
| Worlds & Gamemodes Guide (`IWorldGamemodeService`, `WorldDefinition`…) | How-to | hand | ⚪ |
| Logging & Debugging (`IModLogger`, BepInEx console/log locations) | How-to | hand | ⚪ |
| Worked Examples (GravityGun, Zombies→Worlds, Worlds, NoFeedbackUrl) | Tutorial/Expl | hand → existing mods | 🟡 (Zombies complete) |
| **C# API Reference** | Reference | 🤖 **DocFX** | ⚪ (gated on §7) |
| Security & Trust Model (in-process code exec, permissions descriptive-only) | Explanation | hand | 🟡 (README) |
| Versioning & Compatibility (`supportedGameVersionRange`/loader range) | Explanation | hand | ⚪ |
| Troubleshooting / FAQ | How-to | hand | ⚪ |
| Launcher User Guide (install/detect, enable/disable, plans, diagnostics) | Tutorial/How-to | hand | ⚪ |
| Contributing (Internal) — architecture, `AGENTS.md` rules, verification matrix, links to dartdoc | Explanation | hand + 🤖 **dartdoc** | 🟡 (AGENTS.md) |

**Ship-first priority:** Quickstart → Your First Mod → Manifest Reference → SDK (C#) Reference → Packaging/Mod Anatomy → Troubleshooting. Explanation + Contributing follow.

---

## 6. Rollout Plan

> Effort below is split into **generator/CI setup** vs **prose authoring**, because authoring is the real cost. Authoring estimates assume one writer; treat them as the long pole.

### Phase 0 — Stop the bleed (in-repo Markdown) · setup ~0.5 day · authoring ~0.5–1 day
- Expand `docs\Modding.md` into a real end-to-end Quickstart (template → build → `topiaforge pack` → install → F10).
- `docs/ManifestV5.md` documents the canonical schema-V5 field set: ID-to-range `dependencies` and `optionalDependencies`, `loadBefore`/`loadAfter`, compatibility and host constraints, capabilities, bounded `x-*` metadata, build provenance, optional multiplayer metadata, and the safe mod-id rule.
- Add `CONTRIBUTING.md` with the one-line content-routing rule + PR checklist.
- **Done when:** a new modder can ship a `.topiaforgemod` using only in-repo Markdown.

### Phase 1 — Portal + core hand-written docs · setup ~1–1.5 days · authoring ~4–6 days
- Scaffold Starlight under `website/`; wire task-oriented nav.
- Author the ~9 core pages: Getting Started, Your First Mod, Mod Anatomy, Manifest Reference, Lifecycle/Context, Config, Services Overview, Security & Trust, Troubleshooting. **Budget ~half a day each for good technical prose** — this dominates the phase.
- Source tutorial code snippets from the **CI-scaffolded** `topiaforge new mod` output (the release workflow scaffolds and packs one on every OS) so they can't drift.
- GitHub Actions: build Starlight → deploy to Pages. Add `markdownlint-cli2` + `lychee` link-check on PRs. (Starlight's Pagefind covers portal search out of the box.)
- **Done when:** a credible public site is live at the Pages URL with the modder happy-path complete, portal search working, link-check green in CI.

### Phase 2 — Generated API tier + unified search + service guides · setup ~1 day · authoring ~3–4 days · (after §7 prerequisite)
- Add DocFX build step → `/api/csharp/`; link prominently from modder nav.
- Add dartdoc PowerShell loop → `/api/dart/<pkg>/` + index; link from Contributing only — **or skip per §3.3**.
- Run Pagefind over the **merged** site → one search box across portal + C# (+ Dart, if kept).
- Author Asset Bundle, Prompt Override, **Worlds & Gamemodes** (the most complex surface — budget extra), plus Worked Examples.
- **Done when:** API reference auto-regenerates each build, one search box spans the included trees, all service guides published.

### Phase 3 — Polish & optional versioning · as-needed
- Add `rossjrw/pr-preview-action` (or move deploy to Cloudflare Pages) for PR previews.
- Add Vale with a TopiaForge term list (`mod` vs `plugin`, `.topiaforgemod`, `topiaforge.mod.json`) **only if** terminology drift becomes a real problem at >~15 pages — it's optional polish, not a default.
- Introduce versioning **only when a breaking SDK change lands**: enable `starlight-versions` (or switch to Docusaurus if its maturity is needed), snapshot `latest`, add the version switcher; tie versions to `supportedGameVersionRange`/loader range.
- **Done when:** previews on PRs, (optional) terminology enforced, versioning available if/when needed.

---

## 7. Prerequisite Work in the Codebase

The generated C# API tier is **worthless until comments exist** — this is the single highest-leverage task and blocks Phase 2.

**C# (the public modder surface — do this):**
- In `src\TopiaForge.Mods.Abstractions\TopiaForge.Mods.Abstractions.csproj` add `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. For coverage, prefer a **CI doc-coverage report** (warn on `CS1591`) over a hard `<WarningsAsErrors>CS1591</WarningsAsErrors>` build gate: for a solo dev the hard gate breaks the build on every new undocumented public member, which is friction without much payoff at this stage. Flip to the hard gate later if the SDK formalizes. (`Directory.Build.props` sets `TreatWarningsAsErrors=false`, so either approach is a per-project opt-in; other projects keep `<NoWarn>1591</NoWarn>`.)
- Write `/// <summary>` (and `<param>`/`<returns>` where constructors/methods take arguments) for the public surface across **two files, ~17–18 public types**:
  - V1 authoring contracts: `TopiaForgeMod`, `IModContext`, lifetime-owned services, `OperationResult<T>`, and the optional module contracts. All public members are covered by XML documentation.
  - `Worlds.cs` (only ~3 summaries today): `IWorldGamemodeService`, `GamemodeMenuEntry`, `WorldDefinition`, `GamemodeDefinition`, `WorldLoadRequest`, `WorldSession`, `WorldLoadResult`.
- **Realistic effort:** ~half a day for terse one-line summaries across the SDK; budget **~1 full day** to do the Worlds API (the most complex surface, multi-arg constructors) properly with `<param>`/`<returns>`.

**Dart (internal — lower priority):**
- Add `///` doc comments to the exported surface in `packages\launcher_domain\lib`, starting with `manifest_models.dart` (the authoritative manifest schema/validator). dartdoc itself needs no config — purely a comment-coverage problem.

**Docs-as-code guardrails (Phase 1):**
- `docs/CONTRIBUTING.md` with the content-routing rule + PR checklist.
- GitHub Actions job: `markdownlint-cli2` + `lychee` (fail on broken links) + **scaffold and build a sample mod** (`topiaforge new mod`, as the release workflow already does) so snippets sourced from it can't drift. (RoboPatch's published sample has the classic `api = api` self-assignment bug — exactly what CI-built snippets prevent.)

**Manifest single-source-of-truth (corrected):** the validator lives in Dart (`launcher_domain`, hand-written classes — **no annotations, no `build_runner`/`json_serializable`, and Dart has no usable AOT runtime reflection**, so there is *no* cheap "reflect over `manifest_models.dart`" path). Two workable options, pick one:
1. **Hand-maintain the Manifest Reference table + a CI parse test** (recommended now): a small test asserts every documented canonical key still parses through `ModManifest.fromJson`/`ModDependency`/`ModConflict`, and fails CI if a documented key stops being accepted or a new required field appears.
2. **Author a JSON Schema for `topiaforge.mod.json`** as the single source of truth, render the doc table from it, and validate real manifests against it in CI on **both** the Dart and C# sides. Stronger no-drift guarantee; more upfront work — defer to Phase 3 unless schema-validation is wanted anyway.

---

## 8. Risks, Alternatives & Decision Triggers

| Risk / fork | Trigger to switch | Action |
|---|---|---|
| **Need per-version docs in the near term** | Game/loader versions will diverge soon and you want versioned docs now | Switch portal **Starlight → Docusaurus 3** (mature built-in versioning). This is the one feature that justifies Docusaurus's extra weight. |
| **Want to minimize toolchains** | "Fewer moving parts" outranks best-of-breed polish | Option A: **DocFX as the whole site** (.NET-only conceptual + C# API; dartdoc hosted alongside). Option B: **Writerside** (single tool: guides + API reference + search). Both reduce the stack at the cost of flexibility/theme polish. |
| **DocFX maintainer cadence stalls** | A needed fix/.NET target is unreleased for a long stretch | Fall back to **Sandcastle (SHFB)** — most actively maintained C# generator (2026 releases); heavier, dated look. |
| **Dart tree hurts more than it helps in search** | Cross-search results jarringly jump into the pub.dev-style dartdoc theme; modders rarely want it | **Drop `/api/dart/` from the Pagefind crawl**; keep it as a standalone link from Contributing (§3.3). |
| **Need PR previews / private docs source** | Reviewers want per-PR preview URLs, or you want the docs repo private | Keep the Actions build; **deploy to Cloudflare Pages** (one-line change) — serves prebuilt artifact regardless of repo visibility. (Mind the private-repo self-hosted-runner charge note in §3.4.) |
| **Search quality complaints (no typo tolerance)** | Users report Pagefind misses | Move to **Algolia DocSearch** (now largely self-serve; free for qualifying OSS) once the site is public and sizeable. |
| **Versioning adopted too early** | — | Don't. Start `latest`-only; version only when a breaking SDK change actually ships. Premature versioning is pure overhead. |
| **Doc-coverage gate adds friction** | Build breaks on every new public member (if a hard `CS1591` gate is used) | Use the **CI warn/report** approach in §7 instead of `WarningsAsErrors`; promote to a hard gate only once the SDK stabilizes. |
| **Two-audience leak** (modder vs launcher-user docs) | Install/manage content duplicated | Keep end-user install/management in a single "Launcher User Guide" section; modder docs link to it rather than re-authoring (Fabric's Player-vs-Developer split). |
| **API reference ships empty** | Phase 2 attempted before §7 | Hard-gate Phase 2 on the C# `///` prerequisite. Until then, docs are guide-driven with the example mods as canonical references. |

**Explicitly rejected:**
- **Material for MkDocs** — frozen on 2025-11-05 (9.7.0 final, all Insiders features freed); the maintainer pivoted to a successor, **Zensical**. Not chosen *and* Zensical not chosen: Zensical is too new/unproven to bet a multi-year docs site on today (revisit once it's stable).
- **Sphinx/Read-the-Docs** — Python autodoc useless for C#/Dart; full rST complexity for no payoff.
- **GitBook** — SaaS lock-in, per-seat pricing volatility, awkward raw-HTML embedding.
- **VitePress/Nextra** — no built-in versioning; Nextra also couples to Next.js you don't use.
- **mdBook** — strongest no-Node option, but spartan theme/nav and no versioning make it less credible than Starlight for a public modding portal; reconsider if dropping Node becomes a hard requirement.
- **Publishing private launcher packages to pub.dev** — leaks internals.

---

## 9. Immediate Next Steps (start within a day)

1. **Decide the two forks** (§8): commit to versioning now (→ Docusaurus) or defer (→ Starlight, recommended); and whether to minimize toolchains (DocFX-whole-site / Writerside) or keep best-of-breed (recommended).
2. **C# prerequisite kickoff (completed for V1):** every safe contract project generates XML documentation and treats CS1591 as an error; DocFX publishes the approved public API baselines.
3. **Phase 0 Markdown (completed):** `docs/Modding.md` is the quickstart, `docs/ManifestV5.md` covers the strict V5 contract and package-id regex, and `CONTRIBUTING.md` defines content routing.
4. **CI seed:** add a GitHub Actions job with `markdownlint-cli2` + `lychee` link-check **and a manifest-key parse test** (§7) on PRs.
5. **Scaffold the portal** (chosen generator) under `website/` and wire a deploy-to-Pages workflow with a placeholder home page to lock in hosting/URL early.

---

## 10. UGC Live Content Sync (retired)

Removed in the build-2409 cutover: Robotopia now ships its own browser Creator with live preview and
sharing, so the `TopiaForge.UgcLiveSync` mod, the Automerge sidecar, and the UGC companion package were
deleted rather than retargeted. The world-format contract test survives as
`tests/fixtures/ugc/sample-project.json`.
