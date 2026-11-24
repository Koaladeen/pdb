// Login: pick your name from the list. Users without a password are in with one click.
import { api } from "../api.js";
import { avatar } from "../avatar.js";
import { clear, h, toast } from "../ui.js";

const LAST_KEY = "pdb.lastUser";

function lastUser() {
  try { return localStorage.getItem(LAST_KEY); } catch { return null; }
}

// render(root, onLoggedIn(token), status) - shows the picker; calls back with the token after login.
// status.needed (new installation): hint for the built-in admin.
export async function render(root, onLoggedIn, status = {}) {
  let users = [];
  try {
    users = await api.get("/auth/users");
  } catch (err) {
    toast(err.message, "error");
  }
  const last = lastUser();
  users.sort((a, b) => (b.username === last) - (a.username === last)); // last user first

  const login = async (username, password = "") => {
    const res = await api.post("/auth/login", { username, password, pc_name: "Web" });
    try { localStorage.setItem(LAST_KEY, username); } catch { /* not remembered */ }
    await onLoggedIn(res.token);
  };

  const panelBody = h("div", { class: "panel-body" });
  const showList = () => {
    clear(panelBody,
      status.needed ? h("p", { class: "setup-first" }, "Erste Anmeldung: Administrator wählen, Passwort admin. Danach legst du ein eigenes Passwort fest und richtest PDB ein.") : null,
      h("p", { class: "muted", style: { margin: "0 0 4px" } }, "Wer bist du?"),
      h("div", { class: "user-pick" }, users.map((u) =>
        h("button", {
          type: "button",
          class: `user-tile${u.username === last ? " last" : ""}`,
          onclick: async () => {
            if (u.has_password) return showPassword(u);
            try { await login(u.username); } catch (err) { toast(err.message, "error"); }
          },
        }, avatar(u, 40), h("span", { class: "name" }, u.display_name), u.has_password ? h("span", { class: "lock", title: "mit Passwort" }, "🔒") : null))),
      users.length ? null : h("p", { class: "muted" }, "Keine Benutzer gefunden."));
  };
  const showPassword = (u) => {
    const pass = h("input", { id: "login-pass", type: "password", autocomplete: "current-password" });
    const form = h("form", {
      onsubmit: async (e) => {
        e.preventDefault();
        try { await login(u.username, pass.value); } catch (err) { toast(err.message, "error"); pass.select(); }
      },
    },
    h("div", { class: "row", style: { marginBottom: "12px" } }, avatar(u, 48), h("strong", {}, u.display_name)),
    h("label", { class: "field", for: "login-pass" }, "Passwort", pass),
    h("div", { class: "row section" },
      h("button", { type: "button", onclick: showList }, "‹ Andere Person"),
      h("span", { class: "spacer" }),
      h("button", { class: "primary", type: "submit" }, "Anmelden")));
    clear(panelBody, form);
    pass.focus();
  };

  clear(root, h("div", { class: "login panel" }, h("div", { class: "panel-title" }, "PDB Dateiverwaltung"), panelBody));
  showList();
}
