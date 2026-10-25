# Docs site

## Purpose

The engine's website is built with [Astro Starlight](https://starlight.astro.build/) and themed like the editor: the
same palette, fonts, icons and logo. It has two parts:

- **The landing page** at `/`: the engine at a glance, editor downloads for the visitor's OS, features and screenshots
  (see [Landing page](#landing-page)).
- **The docs** under `/docs/`: the repository's Markdown in `docs/`, searchable.

`just docs` serves it locally with live reload. `.github/workflows/docs.yml` deploys it to GitHub Pages with each
release, at `https://engine.mainframe.games/` (a custom domain; `engine.mainframe.games` is a CNAME to
`mainframe-games.github.io`, set as the repository's Pages custom domain).

The Markdown stays the source of truth. The site is generated from it and adds nothing that GitHub or an IDE would not
show; links in the Markdown keep working in both places.

## Running it

```bash
just docs
```

That serves `http://localhost:4321/mainframe-engine/` (the landing page; the docs are at `…/docs/`) and opens it in
the browser.

- **Requirements:** Node 22.12 or later.
- **First run:** it runs `npm ci` in `docs-site/`. It does the same again after `package-lock.json` changes, for
  example after a pull.
- **Live reload:** changes to `docs/**` re-sync that page and the browser reloads.
- **Arguments:** `--no-open` skips opening the browser; anything else passes through to `astro dev`, for example
  `just docs --host`.

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
| `docs-site/scripts/sync-docs.mjs` | Turns the repository's Markdown into `src/content/docs` and copies images, logo, fonts and the editor's icons; `releaseAssets()` lists the editor downloads |
| `docs-site/scripts/dev.mjs` | `just docs`: install if needed, sync, `astro dev`, watch and re-sync |
| `docs-site/src/styles/theme.css` | The editor theme (see [Theme](#theme)) |
| `docs-site/src/styles/landing.css` | The landing page's layout and animations |
| `docs-site/src/pages/index.astro` | The landing page (see [Landing page](#landing-page)) |
| `docs-site/src/components/landing/` | Its sections in page order (`LandingHero`, `Showcase`, `Daw`, `VideoMode`, `Features`, `Download`), the grid canvas (`grid.ts`), the logo animation (`logo.ts`) and `TablerIcon` |
| `docs-site/src/components/Head.astro` | Starlight's `<head>` plus the Mermaid renderer |
| `docs-site/src/components/Hero.astro` | Starlight's hero, replaced on the landing page by `LandingHero` |
| `docs-site/src/components/SocialIcons.astro` | The header's **Docs** and **Download** links before Starlight's GitHub icon |
| `docs-site/src/plugins/remark-mermaid.mjs` | Turns ` ```mermaid ` blocks into `<pre class="mermaid">` |
| `docs-site/src/content.config.ts` | The Starlight docs collection |

These paths are generated and ignored by git:
- `src/content/docs/`
- `src/assets/` (logo, fonts, icons)
- `public/images/`
- `public/favicon.svg`
- `public/logo-512.png`
- `dist/`
- `node_modules/`

Never edit the generated pages: edit the Markdown in `docs/`.

### Pages and routes

| Source | Route | Sidebar |
|---|---|---|
| `docs-site/src/pages/index.astro` | `/` (the landing page, no sidebar, not in search) | — |
| `docs/README.md` | `/docs/` | Start › Overview |
| `docs/getting-started.md` | `/docs/getting-started/` | Start › Getting started |
| `docs/milestones.md` | `/docs/milestones/` | Start › Milestones |
| `docs/images/brand/README.md` | `/docs/brand/` | Start › Brand |
| `docs/design/*.md` | `/docs/design/<name>/` | Design |
| `docs/design/future/*.md` | `/docs/proposals/<name>/` | Proposals (collapsed; "Proposal:" dropped from labels) |
Not published: the ADRs (`memory/decisions/`), the plans (`docs/superpowers/`) and the working notes
(`memory/context/`). Links to them go to GitHub.

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

### Version

The site title is "Mainframe Engine" plus the engine version the docs describe: the latest release tag (`vX.Y.Z`, see
[Release](release.md)) reachable from the built commit, found with `git describe`. Without a tag it is `dev`;
`DOCS_VERSION` overrides it. The live site is built from the released commit, so it describes exactly that release;
docs merged to `main` since then go live with the next release.

`DOCS_BASE` (default `/`) and `DOCS_SITE` (default `https://engine.mainframe.games`) override the
base path and origin, for example `/mainframe-engine/` + `https://mainframe-games.github.io` to build for the
github.io project site instead of the custom domain.

## Theme

`theme.css` maps Starlight's colour variables onto the editor palette
(`MainframeEngine.Editor/Assets/Editor/theme.rcss`):

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

- **Fonts:** the editor's Lato and Roboto Mono (SIL OFL), copied from `MainframeEngine/Assets/UI/fonts`.
- **Logo and favicons:** the logo is `docs/images/brand/logo.svg`; the favicon is `logo-small.svg`, plus the 512 px PNG.
- **Code blocks:** Expressive Code with GitHub's dark and light themes on the field colour.
- **Mockups:** SVGs drawn on white get a framed white panel in the dark theme.
- **Icons:** the editor's Tabler icons (`MainframeEngine.Editor/Icons/tabler/outline`, MIT), copied to `src/assets/icons`.
- **Theme picker:** Starlight's picker (auto, dark, light) stays.

## Landing page

`src/pages/index.astro` is a `StarlightPage` with the splash template, so it keeps Starlight's header, search, theme
picker and fonts. Starlight's `Hero` component is overridden: on `/` it renders `LandingHero`, on any other page
Starlight's own hero. The sections are marked `not-content`, so Starlight's Markdown styles leave them alone. In page
order: the hero, the showcase, the audio tools, the features, the downloads and a closing call to action.

- **Hero.** A full-width canvas (`grid.ts`) draws the editor's scene grid in perspective: minor and major lines
  scrolling toward the viewer, data pulses running down them, stars above a glowing horizon. The vanishing point
  follows the pointer. Its colours are the `--mf-grid-*` properties in `landing.css`, read again when the theme
  changes. The inline logo (`src/assets/logo.svg`, from `docs/images/brand/logo.svg`) builds itself (`logo.ts` tags
  its parts for `landing.css`): the dots fade in from the core, the "M" draws, its joints pop, the core pulses, and a
  ripple runs out through the dots. The logo tilts toward the pointer.
- **Downloads.** One card per editor archive from `releaseAssets()` in `sync-docs.mjs`:
  `MainframeEditor-X.Y.Z-<rid>` for `osx-arm64`, `win-x64` and `linux-x64` (the names
  [`build/package-editor.sh`](../../build/package-editor.sh) gives them), linked to
  `releases/download/vX.Y.Z/…` of the site's [version](#version). A `dev` build links every card to the latest
  release's page. A script reads the visitor's OS (User-Agent Client Hints, else the user agent), marks that card
  "Your system" and points the hero's **Download** button at its archive; without a match the button scrolls to the
  cards.
- **Features.** One card per subsystem in five groups (rendering, worlds, gameplay, audio, tools), each linking to its
  design doc. A new subsystem gets a card in `Features.astro`.
- **Audio tools.** The music editor and the sound designer, each beside its screenshot (`editor-song.png`,
  `editor-sound-designer.png`) with what it does. Overlays play the screenshots: a playhead crosses the song's
  arrangement and piano roll in time with the Demo song (8 bars at 140 BPM), and a sweep runs over the sound's
  waveform. They are positioned in percent of the 1600 × 960 screenshots (`landing.css`), so a re-captured screenshot
  with a different layout needs them moved.
- **Video mode.** Gameplay recording and the editor's video mode beside `editor-video.png`, in the audio tools' layout
  (the `mf-daw` classes), without overlays.
- **Every feature is on the page.** A user-facing feature gets its card in `Features`, its row in the README's
  Features tables (grouped like the cards) and, when it is something you see in the editor, a screenshot captured by
  `just readme-screenshots` in the README and on this page.
- **Showcase**, first after the hero (the eye-catching sections lead, the downloads come last): the editor screenshot
  in a window frame, the Demo scenes, then the Forest shots with the first one large, all from `docs/images`.
  Clicking one opens it in a `<dialog>`.
- **Motion.** Sections fade and rise into view (`[data-reveal]`, an `IntersectionObserver`); cards light up under the
  pointer. Under `prefers-reduced-motion` nothing animates and the grid is one still frame. The canvas stops drawing
  while it is off screen or the tab is hidden.

## Diagrams

The sync script leaves ` ```mermaid ` fences alone. Then:

1. `remark-mermaid` turns each fence into `<pre class="mermaid">` before Expressive Code highlights code blocks.
2. `Head.astro` loads Mermaid in the browser only on pages that have diagrams.
3. It renders them with the editor palette (`theme: 'base'` with theme variables), and redraws them when the theme
   picker changes.

In a `sequenceDiagram`, `;` ends a statement, so a message containing one fails to parse, on GitHub too. Write `#59;`
for a literal semicolon in message text.

## Deployment

The site deploys **only with a release**: merging to `main` does not publish docs.

- **After a successful release** (`publish.yml`, through `workflow_run`) it checks out the commit the release was
  built from (`workflow_run.head_sha`), builds the site, uploads the Pages artifact and deploys it with
  `actions/deploy-pages` to the `github-pages` environment. The new tag is the version in the title. A failed release
  run (any job, including the nuget.org push) deploys nothing.
- **`workflow_dispatch`** (`gh workflow run docs.yml`) rebuilds and redeploys from `main`'s tip by hand, for example
  after a failed post-release deploy. That publishes whatever docs `main` has.
- **On pull requests** that touch `docs/**`, `docs-site/**`, the editor fonts or icons, or the workflow, it builds the site
  without deploying it. It is not part of `ci-success`, and a docs-only change does not wait on it.
- **History.** The checkout fetches full history and tags (`fetch-depth: 0`) for the version.
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
