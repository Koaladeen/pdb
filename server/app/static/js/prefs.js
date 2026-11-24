// Appearance per user: theme, accent colour, size, density, corners, header style, sidebar,
// pictures, start page. Stored on the server (follows the user to every PC) and cached in the
// browser so the page starts in the right look before login. Applied as data-* attributes on
// <html> (CSS in app.css) plus accent colour variables computed here.
import { api } from "./api.js";
import { setLanguage } from "./i18n.js";
import { debounce } from "./ui.js";

export const DEFAULTS = {
  theme: "auto",
  accent: "#2b579a",
  size: "m",
  density: "normal",
  corners: "normal",
  header: "colored",
  sidebar: true,
  pictures: true,
  start: "search",
  motion: "subtle",
  language: "auto",
};

export const ACCENTS = [
  ["#2b579a", "Blau"],
  ["#1f7a6b", "Petrol"],
  ["#2e7d4f", "Grün"],
  ["#6a4bb5", "Violett"],
  ["#b3261e", "Rot"],
  ["#c25e00", "Orange"],
  ["#4a5263", "Graphit"],
  ["#7a5232", "Braun"],
];

const CACHE = "pdb.prefs";
let current = { ...DEFAULTS, ...readCache() };

function readCache() {
  try { return JSON.parse(localStorage.getItem(CACHE) || "{}"); } catch { return {}; }
}

export function prefs() {
  return current;
}

export function applyPrefs(p) {
  current = { ...DEFAULTS, ...p };
  try { localStorage.setItem(CACHE, JSON.stringify(current)); } catch { /* not remembered */ }
  const root = document.documentElement;
  if (current.theme === "light" || current.theme === "dark") root.dataset.theme = current.theme;
  else delete root.dataset.theme;
  try { localStorage.setItem("pdb.theme", current.theme); } catch { /* index.html reads it */ }
  root.dataset.size = current.size;
  root.dataset.density = current.density;
  root.dataset.corners = current.corners;
  root.dataset.header = current.header;
  root.dataset.sidebar = current.sidebar ? "on" : "off";
  root.dataset.pictures = current.pictures ? "on" : "off";
  root.dataset.motion = current.motion;
  if (setLanguage(current.language)) { location.reload(); return; } // back to German: reload the texts
  applyAccent();
  window.dispatchEvent(new Event("pdb:prefs"));
}

const saveToServer = debounce(() => {
  api.put("/auth/me/preferences", current).catch(() => { /* kept locally; saved next time */ });
}, 600);

// Change some settings: shown at once, saved to the server shortly after.
export function setPrefs(patch) {
  applyPrefs({ ...current, ...patch });
  saveToServer();
}

// ---------------------------------------------------------------- accent colour

function isDark() {
  const t = document.documentElement.dataset.theme;
  if (t === "dark") return true;
  if (t === "light") return false;
  return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function applyAccent() {
  const root = document.documentElement.style;
  const rgb = hexToRgb(current.accent);
  if (!rgb) return;
  const [h, s] = rgbToHsl(rgb);
  let set;
  if (isDark()) {
    // light, readable accent on dark backgrounds; muted bar
    set = {
      "--brand": hsl(h, Math.min(s, 70), 70),
      "--brand-dark": hsl(h, Math.min(s, 70), 82),
      "--brand-soft": mix(rgb, [26, 32, 41], 0.2),
      "--bar": mix(rgb, [18, 22, 29], 0.45),
      "--bar-line": mix(rgb, [18, 22, 29], 0.7),
    };
  } else {
    // the bar carries white text: darken very light accents until it stays readable
    let bar = rgb;
    for (let i = 0; i < 12 && contrast(bar, [255, 255, 255]) < 4.5; i++) bar = bar.map((c) => Math.round(c * 0.88));
    set = {
      "--brand": rgbHex(bar),
      "--brand-dark": rgbHex(bar.map((c) => Math.round(c * 0.75))),
      "--brand-soft": mix(rgb, [255, 255, 255], 0.12),
      "--bar": rgbHex(bar),
      "--bar-line": mix(bar, [255, 255, 255], 0.55),
    };
  }
  for (const [k, v] of Object.entries(set)) root.setProperty(k, v);
}

if (window.matchMedia) {
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => applyAccent());
}

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || "");
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function rgbHex(rgb) {
  return `#${rgb.map((c) => Math.max(0, Math.min(255, c)).toString(16).padStart(2, "0")).join("")}`;
}
// share = how much of `a` remains
function mix(a, b, share) {
  return rgbHex(a.map((c, i) => Math.round(c * share + b[i] * (1 - share))));
}
function rgbToHsl([r, g, b]) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l * 100];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h * 60, s * 100, l * 100];
}
function hsl(h, s, l) {
  return `hsl(${Math.round(h)} ${Math.round(s)}% ${Math.round(l)}%)`;
}
function luminance(rgb) {
  const [r, g, b] = rgb.map((c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function contrast(a, b) {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}
