// Admin: audit log (who did what, when).
import { api } from "../../api.js";
import { userName } from "../../store.js";
import { clear, formatDateTime, h, table } from "../../ui.js";

const ACTIONS = {
  login: "Anmeldung",
  checkout: "Ausgecheckt",
  checkin: "Eingecheckt",
  release: "Freigegeben ohne Speichern",
  lock_takeover: "Abgelaufene Sperre übernommen",
  break_lock: "Sperre aufgehoben",
  nc_export: "NC-Export",
  nc_number: "NC-Nummer vergeben",
  machine_change: "Maschine gewechselt",
  toollist: "Werkzeugliste",
  article_create: "Artikel angelegt",
  article_update: "Artikel geändert",
  article_delete: "Artikel gelöscht",
  operation_create: "Operation angelegt",
  operation_update: "Operation geändert",
  operation_delete: "Operation gelöscht",
  password_set: "Eigenes Passwort gesetzt",
  password_removed: "Eigenes Passwort entfernt",
  avatar_set: "Profilbild gesetzt",
  avatar_delete: "Profilbild entfernt",
  picture_set: "Operationsbild gesetzt",
  picture_delete: "Operationsbild gelöscht",
  customer_create: "Kunde angelegt",
  customer_update: "Kunde geändert",
  customer_deleted: "Kunde gelöscht",
  customer_deactivated: "Kunde deaktiviert",
};

export async function render(root) {
  const entries = await api.get("/admin/audit?limit=500");
  clear(
    root,
    h("div", { class: "page-head" }, h("h1", {}, "Protokoll"), h("span", { class: "muted" }, "letzte 500 Einträge")),
    h("div", { class: "panel" }, table({
      columns: [
        { label: "Zeit", value: (e) => formatDateTime(e.time) },
        { label: "Benutzer", value: (e) => userName(e.user_id) },
        { label: "Aktion", value: (e) => ACTIONS[e.action] || e.action },
        { label: "Artikel", value: (e) => (e.article_id ? h("a", { href: `#/article/${e.article_id}` }, String(e.article_id)) : "") },
        { label: "Details", value: (e) => e.detail },
      ],
      rows: entries,
    })),
  );
}
