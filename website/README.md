# TopiaForge developer site

The public developer portal uses Astro Starlight for task guides and DocFX for the C# API reference.
Files under `docs/` are canonical; `src/content/docs/` is generated and ignored.

## Local build

Install Node.js 24.16 or newer and the repository-pinned .NET SDK, then run:

```sh
dotnet tool restore
cd website
npm ci --ignore-scripts --no-audit --no-fund
npm run check
```

The merged static output is `website/dist/`. The Starlight portal is at `/`, and the DocFX reference
is at `/api/csharp/`.

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
