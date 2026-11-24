// "Mein Profil": tab "Profil" (picture, password) and tab "Darstellung" (appearance).
import { api, files } from "../api.js";
import { avatar, squareImage } from "../avatar.js";
import { isAdmin, store } from "../store.js";
import { attempt, clear, h, modal } from "../ui.js";
import { appearancePanel } from "./appearance.js";

// onChange: lets the top bar show the new picture right away
export function profileDialog(onChange, startTab = "profile") {
  const body = h("div");
  const content = h("div");
  const tabs = [["profile", "Profil", () => body], ["appearance", "Darstellung", appearancePanel]];
  const bar = h("div", { class: "tabs" });
  const show = (key) => {
    bar.querySelectorAll("button").forEach((b) => b.classList.toggle("active", b.dataset.key === key));
    clear(content, tabs.find(([k]) => k === key)[2]());
  };
  tabs.forEach(([key, label]) => bar.append(h("button", { type: "button", "data-key": key, onclick: () => show(key) }, label)));
  const changed = (me) => {
    store.me = { ...store.me, ...me };
    const u = store.users.find((x) => x.id === me.id);
    if (u) u.avatar_at = me.avatar_at;
    draw();
    onChange();
  };

  const draw = () => {
    const me = store.me;
    const file = h("input", { type: "file", accept: "image/*", hidden: true });
    file.addEventListener("change", async () => {
      if (!file.files[0]) return;
      const res = await attempt(async () => files.put("/auth/me/avatar", await squareImage(file.files[0])), "Bild gespeichert");
      if (res) changed(res);
    });

    const current = h("input", { id: "pw-current", type: "password", autocomplete: "current-password" });
    const next = h("input", { id: "pw-new", type: "password", autocomplete: "new-password" });
    const setPassword = async (newPassword) => {
      const res = await attempt(
        () => api.post("/auth/me/password", { current_password: current.value, new_password: newPassword }),
        newPassword ? "Passwort gespeichert" : "Passwort entfernt",
      );
      if (res) changed(res);
    };

    clear(body,
      h("div", { class: "row" },
        avatar(me, 72),
        h("div", { class: "row" },
          h("button", { type: "button", onclick: () => file.click() }, me.avatar_at ? "Bild ändern" : "Bild wählen"),
          me.avatar_at
            ? h("button", { type: "button", class: "danger", onclick: async () => { const r = await attempt(() => api.del("/auth/me/avatar"), "Bild entfernt"); if (r) changed(r); } }, "Bild entfernen")
            : null),
        file),
      h("h2", { class: "section" }, "Passwort"),
      h("p", { class: "muted" }, me.has_password
        ? "Bei der Anmeldung wird dein Passwort abgefragt."
        : "Du meldest dich ohne Passwort an, nur durch Auswahl deines Namens."),
      h("div", { class: "form-grid" },
        me.has_password ? h("label", { class: "field", for: "pw-current" }, "Aktuelles Passwort", current) : null,
        h("label", { class: "field", for: "pw-new" }, me.has_password ? "Neues Passwort" : "Passwort festlegen", next)),
      h("div", { class: "row section" },
        h("button", { type: "button", class: "primary", onclick: () => (next.value.length >= 4 ? setPassword(next.value) : next.focus()) }, "Passwort speichern"),
        me.has_password && !isAdmin()
          ? h("button", { type: "button", onclick: () => setPassword(null) }, "Ohne Passwort anmelden")
          : null),
      isAdmin() ? h("p", { class: "muted" }, "Admins brauchen immer ein Passwort.") : null);
  };
  draw();
  modal({ title: "Mein Profil", body: h("div", {}, bar, content), actions: [{ label: "Schließen", primary: true }] });
  show(startTab);
}
