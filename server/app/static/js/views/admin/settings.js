// Admin: main folders and number counters.
import { api } from "../../api.js";
import { attempt, clear, h, table } from "../../ui.js";

const SETTINGS = [
  ["company_name", "Firmenname (oben in der Leiste)", "z. B. Muster Feinmechanik AG"],
  ["mcam_root", "Mastercam-Hauptordner", "M:\\MDM\\Machines"],
  ["nc_root", "NC-Export-Hauptordner", "H:\\Daten\\BLOCK"],
  ["archive_subfolder", "Unterordner für Versionen", "Revisionen"],
];

export async function render(root) {
  const [settings, counters, machines] = await Promise.all([
    api.get("/admin/settings"),
    api.get("/admin/counters"),
    api.get("/admin/machines"),
  ]);
  const inputs = Object.fromEntries(SETTINGS.map(([key, , ph]) => [key, h("input", { id: `s-${key}`, value: settings[key] ?? "", placeholder: ph })]));

  const counterRows = [
    { name: "mdm", label: "MDM-Nummer" },
    ...machines.map((m) => ({ name: `nc:${m.id}`, label: `NC-Nummer ${m.name}` })),
  ].map((r) => ({ ...r, value: counters[r.name] ?? 0 }));

  const counterInput = (row) => {
    const input = h("input", { type: "number", min: "0", value: row.value, style: { maxWidth: "140px" } });
    const save = h("button", {
      class: "small",
      onclick: () => attempt(() => api.put(`/admin/counters/${encodeURIComponent(row.name)}`, { value: Number(input.value) }), "Zähler gesetzt"),
    }, "Setzen");
    return h("div", { class: "row" }, input, save);
  };

  clear(
    root,
    h("div", { class: "page-head" }, h("h1", {}, "Einstellungen")),
    h("div", { class: "panel" },
      h("div", { class: "panel-title" }, "Hauptordner"),
      h("div", { class: "panel-body" },
        h("p", { class: "muted" }, "Pfade so, wie die PCs sie sehen. Jede Maschine bekommt darunter ihren eigenen Ordner."),
        h("div", { class: "form-grid" }, SETTINGS.map(([key, label]) => h("label", { class: "field", for: `s-${key}` }, label, inputs[key]))),
        h("div", { class: "row section" }, h("button", {
          class: "primary",
          onclick: () => attempt(() => api.put("/admin/settings", Object.fromEntries(Object.entries(inputs).map(([k, el]) => [k, el.value.trim()]))), "Gespeichert"),
        }, "Speichern")))),
    h("div", { class: "panel section" },
      h("div", { class: "panel-title" }, "Nummernkreise"),
      h("div", { class: "panel-body" },
        h("p", { class: "muted" }, "Wert = zuletzt vergebene Nummer; die nächste ist Wert + 1. Vor dem Echtbetrieb über die höchste Nummer aus MDM setzen, damit keine alten Programme überschrieben werden. Belegte Nummern werden übersprungen.")),
      table({
        columns: [
          { label: "Zähler", value: (r) => r.label },
          { label: "Zuletzt vergeben", value: counterInput },
        ],
        rows: counterRows,
      })),
  );
}
