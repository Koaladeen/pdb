// Entry point: layout, navigation and hash routing.
// To add a screen: create js/views/<name>.js exporting render(root, params), add a route below.
import { api, hasToken, setToken, setUnauthorizedHandler } from "./api.js";
import { helperStatus } from "./helper.js";
import { isAdmin, loadSession, store } from "./store.js";
import { clear, closeAllModals, h, toast } from "./ui.js";
import { openCards, sidebar } from "./workspace.js";
import { applyPrefs, prefs } from "./prefs.js";
import { initWorkspace, resetWorkspace } from "./storage.js";
import { themeButton } from "./theme.js";
import { avatar } from "./avatar.js";
import { profileDialog } from "./views/profile.js";

const routes = [
  // [pattern, module path, admin only]
  [/^#\/search$/, "./views/search.js", false],
  [/^#\/article\/(\d+)$/, "./views/article.js", false],
  [/^#\/locks$/, "./views/locks.js", false],
  [/^#\/customers$/, "./views/customers.js", false],
  [/^#\/admin\/users$/, "./views/admin/users.js", true],
  [/^#\/admin\/machines$/, "./views/admin/machines.js", true],
  [/^#\/admin\/fields$/, "./views/admin/fields.js", true],
  [/^#\/admin\/settings$/, "./views/admin/settings.js", true],
  [/^#\/admin\/import$/, "./views/admin/import.js", true],
  [/^#\/admin\/audit$/, "./views/admin/audit.js", true],
];

const nav = [
  ["#/search", "Suche", false],
  ["#/customers", "Kunden", false],
  ["#/locks", "Sperren", false],
  ["#/admin/users", "Benutzer", true],
  ["#/admin/machines", "Maschinen", true],
  ["#/admin/fields", "Felder", true],
  ["#/admin/settings", "Einstellungen", true],
  ["#/admin/import", "Import", true],
  ["#/admin/audit", "Protokoll", true],
];

const appEl = document.getElementById("app");
let mainEl = null;
let cleanup = null; // views may return a function to stop timers when leaving

function layout() {
  mainEl = h("main", { class: "content" });
  const links = nav
    .filter(([, , admin]) => !admin || isAdmin())
    .map(([href, label]) => h("a", { href, "data-href": href }, label));
  clear(
    appEl,
    h(
      "div",
      { class: "topbar" },
      h("span", { class: "logo" }, "PDB", company ? h("span", { class: "logo-company" }, company) : null),
      h("nav", {}, links),
      h(
        "div",
        { class: "who" },
        helperBadge(),
        meButton(),
        themeButton(),
        h("button", { class: "small", onclick: logout }, "Abmelden"),
      ),
    ),
    h("div", { class: "workspace" }, sidebar(), mainEl),
  );
}

// Name and picture in the top bar; opens "Mein Profil".
function meButton() {
  const btn = h("button", { class: "me-btn", title: "Mein Profil: Bild und Passwort" });
  const fill = () => btn.replaceChildren(avatar(store.me, 26), `${store.me.display_name}${isAdmin() ? " (Admin)" : ""}`);
  btn.addEventListener("click", () => profileDialog(fill));
  fill();
  return btn;
}

// Connection lights in the top bar: Helper (tray app on this PC), Server (as the helper sees
// it), Mastercam (bridge loaded?). Hover shows the details. Refreshes every 20 s.
let helperTimer = null;
function helperBadge() {
  const el = h("span", { class: "conn" });
  const light = (state, label) => h("span", { class: `conn-item ${state}` }, h("span", { class: "dot" }), label);
  const update = async () => {
    const s = await helperStatus();
    if (!s) {
      // Installed but not running: pdb://start starts it (Windows asks once).
      // Not there yet: download; the exe runs from where it is saved (no self-install).
      // No download link here on purpose: an unsigned exe downloaded in the browser is
      // (rightly) flagged by endpoint protection. Distribution goes through IT until it is signed.
      el.title = "PDB Helper läuft auf diesem PC nicht.\nStarten: wenn er auf diesem PC schon eingerichtet ist.\nSonst bitte an die IT wenden.";
      el.replaceChildren(
        light("bad", "Helper"),
        h("a", { class: "conn-action", href: "pdb://start", onclick: () => setTimeout(update, 4000) }, "Starten"),
      );
      return;
    }
    if (s.needs_approval) {
      el.title = "PDB Helper läuft, kennt diese Seite aber noch nicht – beim ersten „Öffnen“ am PC erlauben.";
      el.replaceChildren(light("warn", "Helper"));
      return;
    }
    // Not logged in is fine: the first "Öffnen" hands over this page's login.
    const server = s.server_ok === false ? "bad" : "ok";
    const mc = s.mastercam_running === 0 ? "off" : s.mastercam_bridge === s.mastercam_running ? "ok" : "warn";
    el.title = [
      `PDB Helper ${s.version || ""}${s.logged_in ? ` – angemeldet als ${s.user}` : " – übernimmt beim ersten „Öffnen“ die Anmeldung"}`,
      s.server_ok === false ? `Server nicht erreichbar (${s.server})` : `Server verbunden (${s.server})`,
      s.mastercam_text || "",
    ].filter(Boolean).join("\n");
    el.replaceChildren(light("ok", "Helper"), light(server, "Server"), light(mc, "Mastercam"));
  };
  if (helperTimer) clearInterval(helperTimer);
  helperTimer = setInterval(update, 20000);
  update();
  return el;
}

async function logout() {
  resetWorkspace(); // save pending changes, then forget them in this browser
  try { await api.post("/auth/logout"); } catch { /* token may already be gone */ }
  setToken(null);
  showLogin();
}

let company = ""; // from the setup; shown next to the logo

async function showLogin() {
  mainEl = null;
  closeAllModals();
  // new installation: the login shows how to log in as the built-in admin
  let status = { needed: false, company: "" };
  try { status = await api.get("/setup/status"); } catch { /* old server: just log in */ }
  company = status.company || "";
  const view = await import("./views/login.js");
  await view.render(appEl, async (token) => {
    setToken(token);
    await start();
  }, status);
}

async function route() {
  if (!mainEl) return;
  closeAllModals();
  if (cleanup) { cleanup(); cleanup = null; }
  if (!location.hash) {
    // start page from the user's appearance settings
    const cards = openCards();
    if (prefs().start === "last" && cards.length) {
      location.hash = `#/article/${cards[cards.length - 1].id}`;
      return; // hashchange routes again
    }
  }
  const hash = location.hash || "#/search";
  for (const a of appEl.querySelectorAll(".topbar nav a")) {
    a.classList.toggle("active", hash.startsWith(a.dataset.href));
  }
  const match = routes.find(([re]) => re.test(hash));
  if (!match || (match[2] && !isAdmin())) {
    location.hash = "#/search";
    return;
  }
  const [re, path] = match;
  const params = hash.match(re).slice(1);
  clear(mainEl, h("div", { class: "muted" }, "Laden …"));
  try {
    const view = await import(path);
    const result = await view.render(mainEl, ...params);
    if (typeof result === "function") cleanup = result;
    // page-enter animation (CSS decides by the user's "Animationen" setting)
    mainEl.classList.remove("enter");
    void mainEl.offsetWidth; // restart the animation
    mainEl.classList.add("enter");
  } catch (err) {
    clear(mainEl, h("div", { class: "panel panel-body" }, `Fehler: ${err.message}`));
  }
}

async function firstRun(screen, status) {
  mainEl = null;
  closeAllModals();
  const view = await import("./views/setup.js");
  await view[screen](appEl, async (token) => {
    if (token) setToken(token);
    await start();
  }, status);
}

async function start() {
  try {
    await loadSession();
    if (store.me.preferences) applyPrefs(store.me.preferences); // this user's look, on every PC
    initWorkspace(store.me.workspace); // this user's open cards and recent searches
    let status = { needed: false };
    try { status = await api.get("/setup/status"); company = status.company || ""; } catch { /* keep */ }
    // first login of the built-in admin: setup wizard (includes the new password)
    if (status.needed && isAdmin()) return await firstRun("render", status);
    // still the default password (e.g. admin reset by hand): new password first
    if (store.me.must_change_password) return await firstRun("renderPasswordChange", status);
  } catch {
    setToken(null);
    showLogin();
    return;
  }
  layout();
  await route();
}

applyPrefs(prefs()); // cached look (accent colour) until the user's settings are loaded
setUnauthorizedHandler(() => showLogin());
window.addEventListener("hashchange", route);

if (hasToken()) start();
else showLogin();
