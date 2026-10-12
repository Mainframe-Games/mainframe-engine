// The hero logo's animation hooks (the keyframes are in landing.css): the logo SVG (docs/images/brand/logo.svg) is the
// tile, a group of grid dots, the "M" polyline, a group of four joints and the amber core. Each dot gets its distance
// from the core (--d) so the build-in and the idle ripple spread outward from it; the core is wrapped in a group so CSS
// can scale it without replacing its rotate(45) attribute. The pointer tilts the logo.

export function startLogo(root: HTMLElement, host: HTMLElement) {
  const svg = root.querySelector('svg');
  if (!svg) return;
  const [dots, joints] = Array.from(svg.querySelectorAll(':scope > g'));
  const core = svg.querySelector(':scope > rect:last-of-type');
  const mark = svg.querySelector(':scope > polyline');

  dots?.classList.add('mf-logo__dots');
  dots?.querySelectorAll('circle').forEach((dot) => {
    const d = Math.hypot(Number(dot.getAttribute('cx')) - 50, Number(dot.getAttribute('cy')) - 55) / 50;
    (dot as SVGElement).style.setProperty('--d', d.toFixed(3));
  });
  joints?.classList.add('mf-logo__joints');
  mark?.classList.add('mf-logo__mark');
  if (core) {
    const group = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    group.classList.add('mf-logo__core');
    core.replaceWith(group);
    group.append(core);
  }
  root.classList.add('is-ready');

  if (matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  host.addEventListener(
    'pointermove',
    (e) => {
      const box = root.getBoundingClientRect();
      const x = (e.clientX - (box.left + box.width / 2)) / window.innerWidth;
      const y = (e.clientY - (box.top + box.height / 2)) / window.innerHeight;
      root.style.setProperty('--tilt-x', `${(-y * 18).toFixed(2)}deg`);
      root.style.setProperty('--tilt-y', `${(x * 22).toFixed(2)}deg`);
    },
    { passive: true },
  );
  host.addEventListener('pointerleave', () => {
    root.style.removeProperty('--tilt-x');
    root.style.removeProperty('--tilt-y');
  });
}
