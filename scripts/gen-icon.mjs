// Generates the Overtone icon masters in design/icon/.
// One mark everywhere: the first three standing-wave modes of a string (the
// harmonic series), stacked like three lyric lines, each drawn in a colour
// pulled from a record. The tray glyph is the identical mark, cropped tight.
// Run: node scripts/gen-icon.mjs
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const out = join(root, "design", "icon");
mkdirSync(out, { recursive: true });

/** Standing wave of mode n between x0..x1 around yc, as an SVG path. */
export function wave(n, x0, x1, yc, amp, steps = 120) {
  let d = "";
  for (let i = 0; i <= steps; i++) {
    const t = i / steps;
    const x = x0 + (x1 - x0) * t;
    const y = yc - amp * Math.sin(n * Math.PI * t);
    d += `${i ? "L" : "M"}${x.toFixed(1)} ${y.toFixed(1)}`;
  }
  return d;
}

// Geometry on a 1024 grid. Spacing keeps 72-unit strokes from touching.
export const MARK = [
  { n: 1, yc: 326, amp: 80, color: "#FF8A5C" }, // coral
  { n: 2, yc: 526, amp: 64, color: "#FF4F8B" }, // pink
  { n: 3, yc: 726, amp: 52, color: "#9671FF" }, // violet
];
const MONO_OPACITY = [1, 0.72, 0.48];

/** Mark paths. Pass `mono` for a single-colour version (opacity carries the hierarchy). */
function markPaths(strokeWidth, mono) {
  return MARK.map((m, i) => {
    const stroke = mono ?? m.color;
    const op = mono ? ` stroke-opacity="${MONO_OPACITY[i]}"` : "";
    return `    <path d="${wave(m.n, 176, 848, m.yc, m.amp)}" stroke="${stroke}" stroke-width="${strokeWidth}"${op}/>`;
  }).join("\n");
}

function appIcon(strokeWidth) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">
  <defs>
    <radialGradient id="glow" cx="0.2" cy="0.08" r="0.8"><stop offset="0" stop-color="#3A1C3E"/><stop offset="1" stop-color="#3A1C3E" stop-opacity="0"/></radialGradient>
    <linearGradient id="sheen" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity="0.10"/><stop offset="0.4" stop-color="#fff" stop-opacity="0"/></linearGradient>
    <clipPath id="tile"><rect width="1024" height="1024" rx="232"/></clipPath>
  </defs>
  <g clip-path="url(#tile)">
    <rect width="1024" height="1024" fill="#120A19"/>
    <rect width="1024" height="1024" fill="url(#glow)"/>
    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
${markPaths(strokeWidth)}
    </g>
    <rect width="1024" height="1024" fill="url(#sheen)"/>
  </g>
</svg>
`;
}

// Tray: same mark, cropped to its bounds, strokes thickened so it holds at 16 px.
function tray(mono) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="132 132 760 760" width="32" height="32">
  <g fill="none" stroke-linecap="round" stroke-linejoin="round">
${markPaths(92, mono)}
  </g>
</svg>
`;
}

const files = {
  "overtone-app.svg": appIcon(72),          // 64 px and up
  "overtone-app-small.svg": appIcon(84),    // 16–32 px (optically heavier strokes)
  "overtone-tray.svg": tray(),              // Windows tray, full colour
  "overtone-tray-template.svg": tray("#000"), // macOS menu bar template image
};
for (const [name, svg] of Object.entries(files)) writeFileSync(join(out, name), svg);
console.log("wrote", Object.keys(files).map((f) => join(out, f)).join("\n      "));
