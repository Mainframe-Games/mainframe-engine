// The landing hero's background: the editor's scene grid seen in perspective, scrolling toward the viewer, with data
// pulses running down its lines and stars above the horizon. Colours come from CSS custom properties on the hero
// (--mf-grid-*, landing.css), re-read when the theme picker flips data-theme. One static frame under
// prefers-reduced-motion; paused while off screen or in a background tab.

const SPACING = 1; // world units between grid lines
const MAJOR = 5; // every fifth line is a major line, like the editor grid
const CAMERA_HEIGHT = 3;
const FAR = 140;
const SPEED = 2.2; // world units per second
const PULSES = 7;
const STARS = 90;

interface Palette {
  minor: string;
  major: string;
  glow: string;
  star: string;
  fog: [number, number, number];
}

interface Pulse {
  x: number;
  z: number;
  speed: number;
}

export function startGrid(canvas: HTMLCanvasElement, host: HTMLElement) {
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');

  let width = 0;
  let height = 0;
  let palette = readPalette(host, ctx);
  let pointerX = 0;
  let pointerY = 0;
  let easedX = 0;
  let easedY = 0;
  let travelled = 0;
  let last = 0;
  let frame = 0;
  let onScreen = true;

  const random = mulberry32(0x6d61696e);
  const stars = Array.from({ length: STARS }, () => ({
    x: random(),
    y: random(),
    size: 0.4 + random() * 1.1,
    phase: random() * Math.PI * 2,
    rate: 0.6 + random() * 1.8,
  }));
  const pulses: Pulse[] = Array.from({ length: PULSES }, () => spawn(random() * FAR));

  function spawn(z = FAR): Pulse {
    const side = random() < 0.5 ? -1 : 1;
    return { x: side * Math.ceil(random() * 9) * SPACING, z, speed: 6 + random() * 10 };
  }

  function resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    width = canvas.clientWidth;
    height = canvas.clientHeight;
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    ctx!.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (!running()) draw(0);
  }

  function draw(dt: number) {
    const c = ctx!;
    easedX += (pointerX - easedX) * Math.min(1, dt * 3);
    easedY += (pointerY - easedY) * Math.min(1, dt * 3);
    travelled += dt * SPEED;

    c.clearRect(0, 0, width, height);
    const horizon = height * 0.5 + easedY * 14;
    const cx = width / 2 - easedX * 60;
    const focal = height * 0.9;
    const near = (focal * CAMERA_HEIGHT) / Math.max(1, height - horizon);
    const project = (x: number, z: number) => [cx + (focal * x) / z, horizon + (focal * CAMERA_HEIGHT) / z] as const;

    // Sky: twinkling stars above the horizon.
    const time = performance.now() / 1000;
    c.fillStyle = palette.star;
    for (const s of stars) {
      const twinkle = reduced.matches ? 0.6 : 0.5 + 0.5 * Math.sin(time * s.rate + s.phase);
      c.globalAlpha = 0.15 + 0.55 * twinkle * s.y;
      c.beginPath();
      c.arc(s.x * width - easedX * 12, horizon * (1 - s.y * 0.95), s.size, 0, Math.PI * 2);
      c.fill();
    }
    c.globalAlpha = 1;

    // Glow along the horizon.
    const glow = c.createRadialGradient(cx, horizon, 0, cx, horizon, Math.max(width, height) * 0.55);
    glow.addColorStop(0, withAlpha(palette.glow, 0.32));
    glow.addColorStop(0.35, withAlpha(palette.glow, 0.08));
    glow.addColorStop(1, withAlpha(palette.glow, 0));
    c.fillStyle = glow;
    c.fillRect(0, 0, width, height);

    // Lines across (z): world rows the camera moves over.
    c.lineWidth = 1;
    const first = Math.ceil((travelled + near * 0.9) / SPACING);
    const lastRow = Math.floor((travelled + FAR) / SPACING);
    for (let m = first; m <= lastRow; m++) {
      const z = m * SPACING - travelled;
      const [, y] = project(0, z);
      if (y > height + 1) continue;
      const major = m % MAJOR === 0;
      c.strokeStyle = major ? palette.major : palette.minor;
      c.globalAlpha = depthAlpha(z, near) * (major ? 1 : 0.8);
      c.beginPath();
      c.moveTo(0, y);
      c.lineTo(width, y);
      c.stroke();
    }

    // Lines along (x): they meet at the vanishing point.
    const reach = Math.ceil(((width * 0.5 + Math.abs(easedX) * 60) * near * 1.6) / focal / SPACING) + 1;
    for (let i = -reach; i <= reach; i++) {
      const x = i * SPACING;
      const [x0, y0] = project(x, near * 0.6);
      const [x1, y1] = project(x, FAR);
      const major = i % MAJOR === 0;
      c.strokeStyle = major ? palette.major : palette.minor;
      c.globalAlpha = major ? 1 : 0.75;
      c.beginPath();
      c.moveTo(x0, y0);
      c.lineTo(x1, y1);
      c.stroke();
    }

    // Data pulses running toward the viewer along the lines.
    for (let p = 0; p < pulses.length; p++) {
      const pulse = pulses[p];
      pulse.z -= dt * pulse.speed;
      if (pulse.z < near * 0.6) pulses[p] = spawn();
      const [hx, hy] = project(pulse.x, Math.max(pulse.z, 0.01));
      const [tx, ty] = project(pulse.x, pulse.z + 6);
      const alpha = depthAlpha(pulse.z, near);
      const trail = c.createLinearGradient(hx, hy, tx, ty);
      trail.addColorStop(0, withAlpha(palette.glow, alpha));
      trail.addColorStop(1, withAlpha(palette.glow, 0));
      c.globalAlpha = 1;
      c.strokeStyle = trail;
      c.lineWidth = Math.max(1, (2.5 * near) / pulse.z);
      c.beginPath();
      c.moveTo(hx, hy);
      c.lineTo(tx, ty);
      c.stroke();
      c.fillStyle = withAlpha(palette.glow, alpha);
      c.beginPath();
      c.arc(hx, hy, Math.max(1, (3 * near) / pulse.z), 0, Math.PI * 2);
      c.fill();
    }
    c.lineWidth = 1;
    c.globalAlpha = 1;

    // Fog: the far grid fades into the page at the horizon.
    const [r, g, b] = palette.fog;
    const fog = c.createLinearGradient(0, horizon - 2, 0, horizon + (height - horizon) * 0.3);
    fog.addColorStop(0, `rgba(${r},${g},${b},0.95)`);
    fog.addColorStop(1, `rgba(${r},${g},${b},0)`);
    c.fillStyle = fog;
    c.fillRect(0, horizon - 2, width, height - horizon + 2);

    // The horizon itself.
    const line = c.createLinearGradient(0, 0, width, 0);
    line.addColorStop(0, withAlpha(palette.glow, 0));
    line.addColorStop(0.5, withAlpha(palette.glow, 0.7));
    line.addColorStop(1, withAlpha(palette.glow, 0));
    c.fillStyle = line;
    c.fillRect(0, horizon - 0.5, width, 1);
  }

  const running = () => !reduced.matches && onScreen && !document.hidden;

  function tick(now: number) {
    const dt = last ? Math.min((now - last) / 1000, 0.1) : 0;
    last = now;
    draw(dt);
    frame = running() ? requestAnimationFrame(tick) : 0;
  }

  function wake() {
    if (frame || !running()) return;
    last = 0;
    frame = requestAnimationFrame(tick);
  }

  new ResizeObserver(resize).observe(canvas);
  new IntersectionObserver(([entry]) => {
    onScreen = entry.isIntersecting;
    wake();
  }).observe(canvas);
  document.addEventListener('visibilitychange', wake);
  reduced.addEventListener('change', () => (running() ? wake() : draw(0)));
  window.addEventListener(
    'pointermove',
    (e) => {
      pointerX = (e.clientX / window.innerWidth - 0.5) * 2;
      pointerY = (e.clientY / window.innerHeight - 0.5) * 2;
    },
    { passive: true },
  );
  new MutationObserver(() => {
    palette = readPalette(host, ctx);
    if (!running()) draw(0);
  }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

  resize();
  wake();
}

/** Lines fade out toward the horizon. */
function depthAlpha(z: number, near: number) {
  const t = Math.min(1, Math.max(0, (z - near) / (FAR - near)));
  return Math.pow(1 - t, 1.6);
}

function readPalette(host: HTMLElement, ctx: CanvasRenderingContext2D): Palette {
  const css = getComputedStyle(host);
  const get = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback;
  return {
    minor: get('--mf-grid-minor', '#1e3a8a'),
    major: get('--mf-grid-major', '#3b82f6'),
    glow: get('--mf-grid-glow', '#38bdf8'),
    star: get('--mf-grid-star', '#c9cedb'),
    fog: rgb(ctx, get('--sl-color-bg', '#1f2128')),
  };
}

/** Any CSS colour as [r, g, b] (the canvas normalises fillStyle to #rrggbb). */
function rgb(ctx: CanvasRenderingContext2D, color: string): [number, number, number] {
  ctx.fillStyle = '#000';
  ctx.fillStyle = color;
  const hex = String(ctx.fillStyle);
  const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
  return m ? [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)] : [31, 33, 40];
}

const alphaCache = new Map<string, [number, number, number]>();
function withAlpha(color: string, alpha: number) {
  let c = alphaCache.get(color);
  if (!c) {
    const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(color);
    c = m ? [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)] : [56, 189, 248];
    alphaCache.set(color, c);
  }
  return `rgba(${c[0]},${c[1]},${c[2]},${alpha.toFixed(3)})`;
}

/** A small seeded PRNG, so the stars sit in the same places on every visit. */
function mulberry32(seed: number) {
  return () => {
    seed |= 0;
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
