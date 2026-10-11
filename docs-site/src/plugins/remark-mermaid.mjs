// Turns ```mermaid code blocks into <pre class="mermaid"> before Expressive Code sees them; Mermaid.astro renders them
// in the browser (with the site's colours, re-rendered when the theme switches).
const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

export default function remarkMermaid() {
  const walk = (node) => {
    if (!node.children) return;
    node.children = node.children.map((child) => {
      if (child.type === 'code' && child.lang === 'mermaid')
        return { type: 'html', value: `<pre class="mermaid not-content">${escape(child.value)}</pre>` };
      walk(child);
      return child;
    });
  };
  return (tree) => walk(tree);
}
