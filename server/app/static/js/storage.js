// The user's workspace - open cards (sidebar) and recent searches - stored ON THE SERVER per user,
// so it follows the user to every PC and another user on the same PC sees their own.
// load()/save() keep the same simple interface as before; saving is debounced.
import { currentToken } from "./api.js";
import { store } from "./store.js";

const KEYS = { openCards: "cards", recentSearches: "recent" };
let ws = { cards: [], recent: [] };
let timer = null;

// Called after login with what /auth/me returned. Moves lists that older versions kept in this
// browser over to the server once.
export function initWorkspace(fromServer) {
  ws = { cards: [], recent: [], ...(fromServer || {}) };
  let migrated = false;
  for (const [name, key] of Object.entries(KEYS)) {
    if (ws[key].length) continue;
    try {
      const old = JSON.parse(localStorage.getItem(`pdb.${name}.${store.me?.id}`) || "null");
      if (Array.isArray(old) && old.length) { ws[key] = old; migrated = true; }
      localStorage.removeItem(`pdb.${name}.${store.me?.id}`);
    } catch { /* nothing to move */ }
  }
  if (migrated) schedule();
}

export function resetWorkspace() {
  flush();
  ws = { cards: [], recent: [] };
}

export function load(name, fallback) {
  const v = ws[KEYS[name]];
  return Array.isArray(v) ? structuredClone(v) : fallback;
}

export function save(name, value) {
  ws[KEYS[name]] = value;
  schedule();
}

function schedule() {
  clearTimeout(timer);
  timer = setTimeout(flush, 500);
}

// keepalive: also sent when the page is closed right after a change
function flush() {
  if (!timer) return;
  clearTimeout(timer);
  timer = null;
  const token = currentToken();
  if (!token) return;
  const body = JSON.stringify({
    cards: ws.cards.slice(-50).map((c) => ({ id: c.id, title: String(c.title || "").slice(0, 300), subtitle: String(c.subtitle || "").slice(0, 300) })),
    recent: ws.recent.slice(0, 20).map((r) => ({ filters: Object.fromEntries(Object.entries(r.filters || {}).map(([k, v]) => [k, String(v)])), at: Number(r.at) || 0 })),
  });
  fetch("/auth/me/workspace", {
    method: "PUT",
    keepalive: true,
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body,
  }).catch(() => { /* next change saves again */ });
}

window.addEventListener("pagehide", flush);
