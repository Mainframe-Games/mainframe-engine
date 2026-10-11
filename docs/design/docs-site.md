# Docs site

## Purpose

The repository's Markdown (`docs/` and the ADRs in `memory/decisions/`) is published as a searchable website built
with [Astro Starlight](https://starlight.astro.build/). It is themed like the editor: the same palette, fonts and logo.
`just docs` serves it locally with live reload. `.github/workflows/docs.yml` deploys it to GitHub Pages from `main`, at
`https://mainframe-games.github.io/mainframe-engine/`.

The Markdown stays the source of truth. The site is generated from it and adds nothing that GitHub or an IDE would not
show; links in the Markdown keep working in both places.

## Running it

```bash
just docs
```

That serves `http://localhost:4321/mainframe-engine/`.

- **Requirements:** Node 22.12 or later.
- **First run:** it runs `npm ci` in `docs-site/`. It does the same again after `package-lock.json` changes, for
  example after a pull.
- **Live reload:** changes to `docs/**` or `memory/decisions/*.md` re-sync that page and the browser reloads.
- **Arguments** pass through to `astro dev`, for example `just docs --open` or `just docs --host`.

```bash
just docs-build
```

That builds `docs-site/dist` exactly as CI does, including the Pagefind search index, which `astro dev` does not build.
`npm --prefix docs-site run preview` serves the result.

## Layout

| Path | What |
|---|---|
| `docs-site/package.json`, `package-lock.json` | Astro, Starlight, `@astrojs/markdown-remark` (Starlight's peer; the unified processor), Mermaid, sharp, pinned exactly |
| `docs-site/astro.config.mjs` | Site, base path, sidebar, theme hooks, Expressive Code settings |
| `docs-site/scripts/sync-docs.mjs` | Turns the repository's Markdown into `src/content/docs` and copies images, logo and fonts |
| `docs-site/scripts/dev.mjs` | `just docs`: install if needed, sync, `astro dev`, watch and re-sync |
| `docs-site/src/styles/theme.css` | The editor theme (see [Theme](#theme)) |
| `docs-site/src/components/Head.astro` | Starlight's `<head>` plus the Mermaid renderer |
| `docs-site/src/plugins/remark-mermaid.mjs` | Turns ` ```mermaid ` blocks into `<pre class="mermaid">` |
| `docs-site/src/content.config.ts` | The Starlight docs collection |

These paths are generated and ignored by git:
- `src/content/docs/`
- `src/assets/` (logo, fonts)
- `public/images/`
- `public/favicon.svg`
- `public/logo-512.png`
- `dist/`
- `node_modules/`

Never edit the generated pages: edit the Markdown in `docs/`.

### Pages and routes

| Source | Route | Sidebar |
|---|---|---|
| `docs/README.md` | `/` (splash page with the logo) | Start › Overview |
| `docs/milestones.md` | `/milestones/` | Start › Milestones |
| `docs/images/brand/README.md` | `/brand/` | Start › Brand |
| `docs/design/*.md` | `/design/<name>/` | Design |
| `docs/design/future/*.md` | `/proposals/<name>/` | Proposals (collapsed; "Proposal:" dropped from labels) |
| `memory/decisions/*.md` | `/decisions/<name>/` | Decisions (ADRs, collapsed) |
| `docs/superpowers/<kind>/*.md` | `/<kind>/<name>/` | Plans (collapsed) |

`memory/context/` (working notes) is not published.

The sync script does four things to each page:

1. **Title.** The page's first `# ` heading becomes its frontmatter title and is removed from the body.
2. **Edit link.** Every page gets an `editUrl` pointing at its source file on GitHub.
3. **Link rewriting.** Links are rewritten outside code:
   - to another published page → its route, anchors kept (Starlight's heading ids match GitHub's);
   - into `docs/images/` → the copied image;
   - to any other file in the repository (source, tests, workflows, `memory/context`) → `github.com/…/blob/main/…`, or
     `tree/main` for folders.

   Inline links, reference definitions and HTML `src`/`href` are all handled.
4. **Missing targets.** A link to a file that does not exist prints `warning: … link to missing …`. The build does not
   fail, but the warning means the doc has a stale link.

`DOCS_BASE` (default `/mainframe-engine/`) and `DOCS_SITE` (default `https://mainframe-games.github.io`) override the
base path and origin, for example for a custom domain.

## Theme

`theme.css` maps Starlight's colour variables onto the editor palette
(`MainframeEngine.Editor/Content/Editor/theme.rcss`):

| Role | Dark (default) | Light |
|---|---|---|
| Page | panel `#1f2128` | `#ffffff` |
| Header | base `#15171c` | `#f4f5f8` |
| Sidebar | `#1a1c22` | `#f8f9fb` |
| Code and fields | field `#121418` | `#f4f5f8` |
| Lines | `#2f333d` | `#d7dbe4` |
| Text and headings | `#d7dbe4` / `#e8ebf2` | `#2f333d` / `#15171c` |
| Accent | `#3b82f6`; the selected page uses the editor's selection `#1d4ed8` | `#1d4ed8` |

The rest of the editor's look carries over too:

- **Fonts:** the editor's Lato and Roboto Mono (SIL OFL), copied from `MainframeEngine/Content/UI/fonts`.
- **Logo and favicons:** the logo is `docs/images/brand/logo.svg`; the favicon is `logo-small.svg`, plus the 512 px PNG.
- **Code blocks:** Expressive Code with GitHub's dark and light themes on the field colour.
- **Mockups:** SVGs drawn on white get a framed white panel in the dark theme.
- **Theme picker:** Starlight's picker (auto, dark, light) stays.

## Diagrams

The sync script leaves ` ```mermaid ` fences alone. Then:

1. `remark-mermaid` turns each fence into `<pre class="mermaid">` before Expressive Code highlights code blocks.
2. `Head.astro` loads Mermaid in the browser only on pages that have diagrams.
3. It renders them with the editor palette (`theme: 'base'` with theme variables), and redraws them when the theme
   picker changes.

In a `sequenceDiagram`, `;` ends a statement, so a message containing one fails to parse, on GitHub too. Write `#59;`
for a literal semicolon in message text.

## Deployment

`docs.yml` runs when `docs/**`, `memory/decisions/**`, `docs-site/**`, the editor fonts or the workflow change:

- **On pull requests** it builds the site and does not deploy it. It is not part of `ci-success`, and a docs-only
  change does not wait on it.
- **On `main`** (and `workflow_dispatch`) it builds, uploads the Pages artifact and deploys with `actions/deploy-pages`
  to the `github-pages` environment.
- **LFS.** Only `docs/images/**` is fetched from Git LFS (the PNG screenshots), cached by object ID like `ci.yml`.

**One-time setup:** in the repository's Settings › Pages, set the source to **GitHub Actions**. Until then the deploy job
fails with "Pages not enabled".

## Known issues

- **No search in `astro dev`.** Search (Pagefind) exists only in built output; `just docs` shows a notice in the search
  box.
- **Inline HTML-like text.** Text such as `List<T>` outside code spans is parsed as HTML by the Markdown processor, as
  on GitHub; it should be in backticks.
- **Deleted sources** (the dev watcher removes them) and renamed folders under `docs/images` need a restart of
  `just docs` if the browser keeps a stale page.
