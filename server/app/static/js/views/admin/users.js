// Admin: users, roles, password or not, profile picture, machine rights.
import { api, files } from "../../api.js";
import { avatar, squareImage } from "../../avatar.js";
import { refreshCatalog, store } from "../../store.js";
import { attempt, badge, clear, confirmDialog, h, modal, table } from "../../ui.js";

export async function render(root) {
  const [users, machines] = await Promise.all([api.get("/admin/users"), api.get("/admin/machines")]);
  const reload = async () => { await refreshCatalog(); render(root); };
  const machineNames = new Map(machines.map((m) => [m.id, m.name]));

  clear(
    root,
    h("div", { class: "page-head" }, h("h1", {}, "Benutzer"), h("button", { class: "primary", onclick: () => userDialog(null, machines, reload) }, "+ Neuer Benutzer")),
    h("div", { class: "panel" }, table({
      columns: [
        { label: "", value: (u) => avatar(u, 28) },
        { label: "Name", value: (u) => u.display_name },
        { label: "Benutzername", value: (u) => u.username },
        { label: "Rolle", value: (u) => (u.role === "admin" ? badge("Admin") : "Benutzer") },
        { label: "Passwort", value: (u) => (u.has_password ? "ja" : h("span", { class: "muted" }, "ohne")) },
        { label: "Status", value: (u) => (u.active ? badge("aktiv", "ok") : badge("inaktiv", "bad")) },
        { label: "Maschinen", value: (u) => (u.role === "admin" ? "alle" : u.machine_ids.map((id) => machineNames.get(id)).join(", ")) },
      ],
      rows: users,
      onRowClick: (u) => userDialog(u, machines, reload),
    })),
  );
}

function userDialog(user, machines, reload) {
  const isNew = !user;
  let picture = null; // chosen new profile picture (Blob), uploaded on save
  let removePicture = false;

  const username = h("input", { id: "u-name", value: user?.username ?? "", disabled: !isNew });
  const display = h("input", { id: "u-display", value: user?.display_name ?? "" });
  const password = h("input", {
    id: "u-pass",
    type: "password",
    autocomplete: "new-password",
    placeholder: isNew ? "mind. 4 Zeichen" : user.has_password ? "leer lassen = unverändert" : "",
  });
  const noPassword = h("input", { type: "checkbox", checked: isNew ? true : !user.has_password });
  const role = h("select", { id: "u-role" },
    h("option", { value: "user", selected: user?.role !== "admin" }, "Benutzer"),
    h("option", { value: "admin", selected: user?.role === "admin" }, "Admin"));
  const active = h("input", { type: "checkbox", checked: user ? user.active : true });
  const boxes = machines.filter((m) => m.active).map((m) => {
    const cb = h("input", { type: "checkbox", value: m.id, checked: user ? user.machine_ids.includes(m.id) : false });
    return { cb, el: h("label", { class: "check" }, cb, m.name) };
  });

  // Admins always need a password: lock the "without password" box while role is admin.
  const syncPassword = () => {
    if (role.value === "admin") noPassword.checked = false;
    noPassword.disabled = role.value === "admin";
    password.disabled = noPassword.checked;
    if (noPassword.checked) password.value = "";
  };
  role.addEventListener("change", syncPassword);
  noPassword.addEventListener("change", syncPassword);
  syncPassword();

  // profile picture
  const preview = h("span");
  const file = h("input", { type: "file", accept: "image/*", hidden: true });
  const showPreview = () => {
    if (picture) {
      const img = h("img", { class: "avatar", style: { width: "56px", height: "56px" }, src: URL.createObjectURL(picture), alt: "" });
      preview.replaceChildren(img);
    } else {
      preview.replaceChildren(avatar(removePicture ? { display_name: display.value } : (user ?? { display_name: display.value }), 56));
    }
  };
  file.addEventListener("change", async () => {
    if (!file.files[0]) return;
    picture = await squareImage(file.files[0]);
    removePicture = false;
    showPreview();
  });
  showPreview();

  const body = h("div", {},
    h("div", { class: "row", style: { marginBottom: "12px" } },
      preview,
      h("button", { type: "button", onclick: () => file.click() }, "Bild wählen"),
      user?.avatar_at ? h("button", { type: "button", class: "danger", onclick: () => { removePicture = true; picture = null; showPreview(); } }, "Bild entfernen") : null,
      file),
    h("div", { class: "form-grid" },
      h("label", { class: "field", for: "u-display" }, "Anzeigename", display),
      h("label", { class: "field", for: "u-name" }, "Benutzername (nicht änderbar)", username),
      h("label", { class: "field", for: "u-role" }, "Rolle", role),
      h("label", { class: "field", for: "u-pass" }, "Passwort", password),
      h("label", { class: "check wide" }, noPassword, "Ohne Passwort – Anmeldung nur durch Auswahl des Namens (nicht für Admins)"),
      isNew ? null : h("label", { class: "check" }, active, "aktiv")),
    h("h2", { class: "section" }, "Maschinenrechte"),
    h("p", { class: "muted" }, "Admins dürfen immer alle Maschinen."),
    h("div", { style: { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))", gap: "6px" } }, boxes.map((b) => b.el)),
  );

  const machineIds = () => boxes.filter((b) => b.cb.checked).map((b) => Number(b.cb.value));
  const actions = [
    !isNew && user.id !== store.me.id
      ? {
        label: "Löschen",
        danger: true,
        onClick: async () => {
          if (!(await confirmDialog("Benutzer löschen", `${user.display_name} löschen? Wer schon gearbeitet hat, wird nur deaktiviert.`, "Löschen", true))) return false;
          const res = await attempt(() => api.del(`/admin/users/${user.id}`));
          if (!res) return false;
          reload();
        },
      }
      : null,
    { label: "Abbrechen" },
    {
      label: "Speichern",
      primary: true,
      onClick: async () => {
        if (!noPassword.checked && !password.value && (isNew || !user.has_password)) {
          password.focus();
          return false;
        }
        const ok = await attempt(async () => {
          let saved;
          if (isNew) {
            saved = await api.post("/admin/users", {
              username: username.value.trim(),
              display_name: display.value.trim(),
              password: noPassword.checked ? null : password.value,
              role: role.value,
              machine_ids: machineIds(),
            });
          } else {
            const patch = { display_name: display.value.trim(), role: role.value, active: active.checked };
            if (noPassword.checked && user.has_password) patch.remove_password = true;
            else if (password.value) patch.password = password.value;
            await api.patch(`/admin/users/${user.id}`, patch);
            saved = await api.put(`/admin/users/${user.id}/machines`, { machine_ids: machineIds() });
          }
          if (picture) await files.put(`/admin/users/${saved.id}/avatar`, picture);
          else if (removePicture) await api.del(`/admin/users/${saved.id}/avatar`);
          return saved;
        }, "Gespeichert");
        if (!ok) return false;
        if (store.me.id === ok.id) store.me = { ...store.me, ...ok };
        reload();
      },
    },
  ].filter(Boolean);
  modal({ title: isNew ? "Neuer Benutzer" : user.display_name, body, actions });
}
