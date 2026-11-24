// Lock overview: who has which file open. Refreshes itself; admins can break locks.
import { api } from "../api.js";
import { isAdmin } from "../store.js";
import { attempt, badge, clear, confirmDialog, formatDateTime, h, table } from "../ui.js";

const REFRESH_MS = 15000;

export async function render(root) {
  const list = h("div", { class: "panel" });
  clear(root, h("div", { class: "page-head" }, h("h1", {}, "Gesperrte Dateien"), h("span", { class: "muted" }, "aktualisiert alle 15 s")), list);

  async function load() {
    const locks = await attempt(() => api.get("/locks"));
    if (!locks) return;
    clear(
      list,
      table({
        columns: [
          { label: "MDM-Nr.", class: "num", value: (l) => String(l.mdm_number).padStart(8, "0") },
          { label: "Teil", value: (l) => l.part },
          { label: "Maschine", value: (l) => l.machine },
          { label: "Benutzer", value: (l) => l.user },
          { label: "PC", value: (l) => l.pc_name },
          { label: "Seit", value: (l) => formatDateTime(l.locked_at) },
          { label: "Lebenszeichen", value: (l) => [formatDateTime(l.last_heartbeat), " ", l.stale ? badge("abgelaufen", "warn") : badge("aktiv", "ok")] },
          {
            label: "",
            value: (l) => isAdmin()
              ? h("button", { class: "small danger", onclick: (e) => { e.stopPropagation(); breakLock(l, load); } }, "Aufheben")
              : "",
          },
        ],
        rows: locks,
        empty: "Keine Datei gesperrt",
      }),
    );
  }

  await load();
  const timer = setInterval(load, REFRESH_MS);
  return () => clearInterval(timer);
}

async function breakLock(lock, reload) {
  const ok = await confirmDialog("Sperre aufheben", `Sperre von ${lock.user} an ${lock.pc_name} aufheben?`, "Aufheben", true);
  if (ok && (await attempt(() => api.post(`/admin/break-lock/${lock.operation_id}`), "Sperre aufgehoben"))) reload();
}
