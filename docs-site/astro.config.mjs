// The documentation site (docs/design/docs-site.md): Astro Starlight over the repository's Markdown, which
// scripts/sync-docs.mjs turns into src/content/docs. Deployed to GitHub Pages by .github/workflows/docs.yml.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { unified } from '@astrojs/markdown-remark';
import remarkMermaid from './src/plugins/remark-mermaid.mjs';
import { base, repoUrl, version } from './scripts/sync-docs.mjs';

export default defineConfig({
  site: process.env.DOCS_SITE ?? 'https://mainframe-games.github.io',
  base,
  trailingSlash: 'always',
  markdown: { processor: unified({ remarkPlugins: [remarkMermaid] }) },
  // Mermaid is one large lazy chunk, loaded only on pages with diagrams.
  vite: { build: { chunkSizeWarningLimit: 4096 } },
  integrations: [
    starlight({
      title: `Mainframe Engine ${version}`,
      description: 'Design documentation for Mainframe Engine: a C# game engine on Vulkan with a Godot-style node tree.',
      logo: { src: './src/assets/logo.svg', alt: 'Mainframe Engine' },
      favicon: '/favicon.svg',
      head: [
        { tag: 'link', attrs: { rel: 'icon', href: base + 'logo-512.png', type: 'image/png', sizes: '512x512' } },
        { tag: 'meta', attrs: { name: 'theme-color', content: '#15171c' } },
      ],
      social: [{ icon: 'github', label: 'GitHub', href: repoUrl }],
      customCss: ['./src/styles/theme.css'],
      components: { Head: './src/components/Head.astro' },
      tableOfContents: { minHeadingLevel: 2, maxHeadingLevel: 3 },
      expressiveCode: {
        themes: ['github-dark-default', 'github-light'],
        shiki: { langAlias: { slang: 'hlsl' } }, // the engine's shaders (Slang is HLSL-derived)
        styleOverrides: {
          borderRadius: '6px',
          borderColor: 'var(--sl-color-hairline)',
          codeBackground: 'var(--mf-field)',
          codeFontFamily: "'Roboto Mono', ui-monospace, monospace",
          uiFontFamily: "'Lato', sans-serif",
          frames: { editorTabBarBackground: 'var(--mf-raised)', terminalTitlebarBackground: 'var(--mf-raised)' },
        },
      },
      sidebar: [
        {
          label: 'Start',
          items: [
            { label: 'Overview', link: '/' },
            { label: 'Milestones', slug: 'milestones' },
            { label: 'Brand', slug: 'brand' },
          ],
        },
        { label: 'Design', items: [{ autogenerate: { directory: 'design' } }] },
        { label: 'Proposals', collapsed: true, items: [{ autogenerate: { directory: 'proposals' } }] },
      ],
    }),
  ],
});
