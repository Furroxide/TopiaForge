# TopiaForge developer site

The public developer portal uses Astro Starlight for task guides, DocFX for the C# API reference, and dartdoc for
the three shared launcher packages. Files under `docs/` are canonical; `src/content/docs/` is generated and ignored.

## Local build

Install Node.js 24.16 or newer, the repository-pinned .NET SDK, and Flutter 3.41.4 (Dart 3.11.1), then run:

```sh
dotnet tool restore
cd website
npm ci --ignore-scripts --no-audit --no-fund
npm run check
```

The merged static output is `website/dist/`. The generated routes are:

- `/` — Starlight guides and redirects.
- `/api/csharp/` — DocFX reference.
- `/api/dart/` — dartdoc landing page and the `launcher_domain`, `launcher_data`, and `launcher_ui` references.
- `/pagefind/` — a directly pinned Pagefind index rebuilt after all four generators finish.
- `/source/` — mirrored capability and live-acceptance evidence.

`npm run check` validates dartdoc links and rejects generator warnings, marks the DocFX and dartdoc content bodies,
rebuilds unified search, and checks every built local link last. Application and CLI internals are not part of the Dart
reference.

## Pages publication

The Pages workflow publishes one freshly assembled artifact to `https://docs.topiaforge.dev` after a successful
`main` push CI run, on a manual `main` dispatch, and after a stable release refresh dispatch. The artifact adds the
official `/registry/index.json` feed and includes `/manual-releases.json` only after a complete stable release exists.
The assembler rejects links, special files, collisions, malformed feed shapes, stale output, and the retired root
`index.json` before upload. The custom domain remains configured in GitHub Pages settings; Actions-based publication
does not add a `CNAME` file.

## Add or change a page

1. Edit the canonical Markdown file under `docs/`.
2. Add its source/output mapping and sidebar entry when it is a new public page.
3. Use a directive such as the following for sample code:

   ```text
   <!-- topiaforge-snippet path="templates/mod/minimal/{{TYPE_NAME}}Mod.cs" -->
   ```

4. Run `npm run check:content` for fast published-link, snippet, and retired-contract validation. Run
   `npm run check:markdown-links` to audit every repository Markdown target and anchor.
5. Run `npm run check` before handoff.

`npm run docs:prepare` explicitly regenerates the Starlight content. It is not an npm lifecycle hook, so
`npm install` and `npm ci` never mutate generated documentation as a side effect.

The generator substitutes readable example identity tokens only after reading the template. It rejects a
snippet unless that template has a packaged-SDK main project, a testing-kit NUnit project, and an entry in the
release scaffold CI matrix. The documentation job waits for that matrix to scaffold, build, test, pack, and
validate all seven templates, so published snippets cannot drift into a second uncompiled copy. Linked
non-Markdown evidence, such as the capability matrix JSON, is mirrored into the static output from the same
checkout so a deployed guide never points at evidence from another revision.
