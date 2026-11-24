// Admin: machines with their folders, NC naming and post settings.
import { api } from "../../api.js";
import { refreshCatalog } from "../../store.js";
import { attempt, badge, clear, confirmDialog, h, modal, table } from "../../ui.js";

export async function render(root) {
  const [machines, settings] = await Promise.all([api.get("/admin/machines"), api.get("/admin/settings")]);
  const reload = async () => { await refreshCatalog(); render(root); };

  clear(
    root,
    h("div", { class: "page-head" },
      h("h1", {}, "Maschinen"),
      h("button", { class: "primary", onclick: () => machineDialog(null, settings, reload) }, "+ Neue Maschine")),
    h("div", { class: "panel" }, table({
      columns: [
        { label: "Name", value: (m) => m.name },
        { label: "Mastercam-Ordner", value: (m) => h("code", {}, m.mcam_folder) },
        { label: "NC-Ordner", value: (m) => h("code", {}, m.nc_folder) },
        { label: "NC-Dateiname", value: (m) => (m.nc_naming === "nc" ? `NC-Nummer${m.nc_extension}` : `MDM-Nummer${m.nc_extension}`) },
        { label: "Maschinendefinition", value: (m) => (m.machine_definition ? h("code", { title: m.machine_definition }, m.machine_definition.split("\\").pop()) : "") },
        { label: "Letzte NC-Nr.", class: "num", value: (m) => String(m.nc_counter) },
        { label: "Status", value: (m) => (m.active ? badge("aktiv", "ok") : badge("inaktiv", "bad")) },
      ],
      rows: machines,
      onRowClick: (m) => machineDialog(m, settings, reload),
      empty: "Noch keine Maschinen angelegt",
    })),
  );
}

function machineDialog(m, settings, reload) {
  const isNew = !m;
  const inputs = {
    name: h("input", { value: m?.name ?? "", placeholder: "DMU 50" }),
    mcam_folder: h("input", { value: m?.mcam_folder ?? "", placeholder: "DMU50" }),
    nc_folder: h("input", { value: m?.nc_folder ?? "", placeholder: "DMU50_NC" }),
    nc_naming: h("select", {},
      h("option", { value: "mdm", selected: m?.nc_naming !== "nc" }, "MDM-Nummer (z. B. 00084197.H)"),
      h("option", { value: "nc", selected: m?.nc_naming === "nc" }, "NC-Programmnummer (FANUC, z. B. 1101.NC)")),
    nc_extension: h("input", { value: m?.nc_extension ?? ".H" }),
    nc_number_max: h("input", { type: "number", min: "1", value: m?.nc_number_max ?? "" }),
    machine_definition: h("input", {
      value: m?.machine_definition ?? "",
      placeholder: "M:\\Mastercam\\CNC_MACHINES\\DMU50-TNC640.mcam-mmd",
    }),
    header_template: h("textarea", {}, m?.header_template ?? ""),
  };
  const active = h("input", { type: "checkbox", checked: m ? m.active : true });
  const preview = h("div", { class: "paths muted wide" });
  const updatePreview = () => {
    preview.replaceChildren(
      h("div", {}, `${settings.mcam_root}\\${inputs.mcam_folder.value || "…"}\\MDM84197\\00084197.mcam`),
      h("div", {}, `${settings.nc_root}\\${inputs.nc_folder.value || "…"}\\${inputs.nc_naming.value === "nc" ? "1101" : "00084197"}${inputs.nc_extension.value}`),
    );
  };
  Object.values(inputs).forEach((el) => el.addEventListener("input", updatePreview));
  updatePreview();

  const f = (label, el, wide = false) => h("label", { class: `field${wide ? " wide" : ""}` }, label, el);
  const body = h("div", { class: "form-grid" },
    f("Name", inputs.name, true),
    f("Ordner unter Mastercam-Hauptordner", inputs.mcam_folder),
    f("Ordner unter NC-Hauptordner", inputs.nc_folder),
    f("NC-Dateiname", inputs.nc_naming),
    f("Dateiendung", inputs.nc_extension),
    f("Höchste NC-Nummer (leer = keine Grenze)", inputs.nc_number_max),
    f("Maschinendefinition (.mcam-mmd, enthält den Postprozessor)", inputs.machine_definition, true),
    f("Kopfzeilen-Vorlage (optional)", inputs.header_template, true),
    isNew ? null : h("label", { class: "check" }, active, "aktiv"),
    h("div", { class: "wide" }, h("div", { class: "muted" }, "Beispielpfade"), preview),
  );

  const values = () => ({
    name: inputs.name.value.trim(),
    mcam_folder: inputs.mcam_folder.value.trim(),
    nc_folder: inputs.nc_folder.value.trim(),
    nc_naming: inputs.nc_naming.value,
    nc_extension: inputs.nc_extension.value.trim(),
    nc_number_max: inputs.nc_number_max.value === "" ? null : Number(inputs.nc_number_max.value),
    machine_definition: inputs.machine_definition.value.trim().replace(/^"(.*)"$/, "$1"), // pasted "Copy as path" has quotes
    header_template: inputs.header_template.value,
    active: active.checked,
  });

  modal({
    title: isNew ? "Neue Maschine" : m.name,
    body,
    actions: [
      isNew ? null : {
        label: "Löschen",
        danger: true,
        onClick: async () => {
          if (!(await confirmDialog("Maschine löschen", `${m.name} löschen? Mit Operationen wird sie nur deaktiviert.`, "Löschen", true))) return false;
          const res = await attempt(() => api.del(`/admin/machines/${m.id}`));
          if (!res) return false;
          reload();
        },
      },
      { label: "Abbrechen" },
      {
        label: "Speichern",
        primary: true,
        onClick: async () => {
          const ok = await attempt(
            () => (isNew ? api.post("/admin/machines", values()) : api.patch(`/admin/machines/${m.id}`, values())),
            "Gespeichert",
          );
          if (!ok) return false;
          reload();
        },
      },
    ].filter(Boolean),
  });
}
