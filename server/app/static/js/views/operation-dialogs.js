// Dialogs for one operation (Prozessdaten): create, edit, files, versions, NC number,
// machine change, NC export check. Kept separate so the add-in features can grow here.
import { api } from "../api.js";
import { fieldForm } from "../fields.js";
import { changeMachineViaHelper, openInMastercam } from "../helper.js";
import { deletePicture, pictureImg, uploadPicture } from "../pictures.js";
import { isAdmin, store } from "../store.js";
import { attempt, badge, clear, confirmDialog, formatDateTime, h, modal, table, toast } from "../ui.js";

function allowedMachines() {
  return store.machines.filter((m) => m.allowed);
}

export function newOperationDialog(article, reload) {
  const machines = allowedMachines();
  if (!machines.length) {
    modal({ title: "Neue Operation", narrow: true, body: h("p", {}, "Sie haben für keine Maschine eine Berechtigung."), actions: [{ label: "OK" }] });
    return;
  }
  const highest = Math.max(0, ...article.operations.map((o) => o.op_number));
  const machine = h("select", { id: "new-op-machine" }, machines.map((m) => h("option", { value: m.id }, m.name)));
  const opNumber = h("input", { id: "new-op-nr", type: "number", min: "0", value: (Math.floor(highest / 10) + 1) * 10 });
  const assignNc = h("input", { type: "checkbox" });
  const openNow = h("input", { type: "checkbox", checked: true });
  const form = fieldForm("operation");
  modal({
    title: "Neue Operation",
    body: h(
      "div",
      {},
      h("div", { class: "form-grid" },
        h("label", { class: "field", for: "new-op-machine" }, "Maschine", machine),
        h("label", { class: "field", for: "new-op-nr" }, "Nr.", opNumber),
        h("label", { class: "check wide" }, assignNc, "NC-Programmnummer sofort vergeben (bei FANUC-Maschinen automatisch)"),
        h("label", { class: "check wide" }, openNow, "Gleich in Mastercam öffnen – neue Datei mit der Maschinendefinition wird angelegt")),
      h("div", { class: "section" }, form.el),
    ),
    actions: [
      { label: "Abbrechen" },
      {
        label: "Anlegen",
        primary: true,
        onClick: async () => {
          const op = await attempt(
            () => api.post(`/articles/${article.id}/operations`, {
              machine_id: Number(machine.value),
              op_number: opNumber.value === "" ? null : Number(opNumber.value),
              assign_nc_number: assignNc.checked,
              data: form.values(),
            }),
            "Operation angelegt",
          );
          if (!op) return false;
          if (openNow.checked) openInMastercam(op.id);
          reload();
        },
      },
    ],
  });
}

export async function operationDialog(id, reload) {
  const op = await attempt(() => api.get(`/operations/${id}`));
  if (!op) return;
  const body = h("div");
  const data = dataTab(op); // created once so edits survive switching tabs
  const tabs = [
    ["Daten", () => data],
    ["Dateien", () => pathsTab(op)],
    ["Versionen", () => revisionsTab(op)],
    ["Bild", () => pictureTab(op, reload)],
  ];
  const tabBar = h("div", { class: "tabs" });
  const content = h("div");
  const show = (i) => {
    tabBar.querySelectorAll("button").forEach((b, j) => b.classList.toggle("active", i === j));
    Promise.resolve(tabs[i][1]().el).then((el) => clear(content, el));
  };
  tabs.forEach(([label], i) => tabBar.append(h("button", { type: "button", onclick: () => show(i) }, label)));
  body.append(
    h("div", { class: "row", style: { marginBottom: "12px" } },
      badge(`MDM ${op.mdm_number_text}`),
      badge(op.machine),
      op.nc_number != null ? badge(`NC ${op.nc_number}`) : null,
      badge(`Version ${op.current_revision}`),
      op.lock
        ? badge(`🔒 ${op.lock.user} an ${op.lock.pc_name} seit ${formatDateTime(op.lock.locked_at)}${op.lock.stale ? " (abgelaufen)" : ""}`, op.lock.stale ? "warn" : "bad")
        : badge("frei", "ok")),
    tabBar,
    content,
  );

  const done = (msg) => { m.close(); reload(); if (msg) operationDialog(id, reload); };
  const actions = [
    { label: "In Mastercam öffnen", onClick: () => { openInMastercam(op.id).then(() => done(true)); return false; } },
    op.nc_number == null
      ? { label: "NC-Nummer vergeben", onClick: async () => { if (await attempt(() => api.post(`/operations/${id}/nc-number`), "NC-Nummer vergeben")) done(true); return false; } }
      : null,
    { label: "Maschine wechseln", onClick: () => { machineDialog(op, () => done(true)); return false; } },
    { label: "NC-Export prüfen", onClick: () => { exportDialog(op); return false; } },
    op.lock && isAdmin()
      ? { label: "Sperre aufheben", danger: true, onClick: async () => { if (await breakLock(op)) done(true); return false; } }
      : null,
    isAdmin() && !op.lock
      ? { label: "Löschen", danger: true, onClick: async () => { if (await deleteOperation(op)) done(false); return false; } }
      : null,
    {
      label: "Speichern",
      primary: true,
      onClick: async () => {
        const changes = data.values();
        if (!changes) return;
        const ok = await attempt(() => api.patch(`/operations/${id}`, changes), "Gespeichert");
        if (!ok) return false;
        reload();
      },
    },
  ].filter(Boolean);

  const m = modal({ title: `Prozessdaten – ${op.data.prozessbeschreibung || `Nr. ${op.op_number}`}`, body, actions });
  show(0);
}

function dataTab(op) {
  const opNumber = h("input", { id: "op-nr", type: "number", min: "0", value: op.op_number });
  const form = fieldForm("operation", op.data);
  return {
    el: h("div", {}, h("div", { class: "form-grid" }, h("label", { class: "field", for: "op-nr" }, "Nr.", opNumber)), h("div", { class: "section" }, form.el)),
    values() {
      const out = { data: form.values() };
      if (Number(opNumber.value) !== op.op_number && opNumber.value !== "") out.op_number = Number(opNumber.value);
      return Object.keys(out.data).length || out.op_number !== undefined ? out : null;
    },
  };
}

const PATH_LABELS = {
  folder: "Ordner",
  mcam: "Mastercam",
  nc_posted: "NC (gepostet)",
  nc_old: "NC (alt)",
  tool_list: "Werkzeugliste",
  picture: "Bild",
  next_archive: "Nächste Version",
  nc_export: "NC-Export",
  nc_info: "Info-Datei",
};

function pathsBlock(paths) {
  return h("div", { class: "paths" }, Object.entries(paths).map(([k, v]) =>
    h("div", { class: "kv" }, h("div", { class: "k" }, PATH_LABELS[k] || k), h("div", { class: "v" }, v))));
}

// Picture of the operation. Normally the PDB Helper uploads Mastercam's Pic<nr>.png.cam;
// here it can also be set by hand: choose a file, drop it, or paste a screenshot (Ctrl+V).
function pictureTab(op, reload) {
  const box = h("div");
  let current = op;
  const draw = () => {
    const input = h("input", { type: "file", accept: "image/png,image/jpeg", hidden: true });
    const drop = h("div", { class: "picture-drop", tabindex: "0" },
      "Bild hierher ziehen, mit Strg+V einfügen oder ",
      h("button", { type: "button", class: "small", onclick: () => input.click() }, "Datei wählen"));
    const upload = async (blob) => {
      if (!blob) return;
      const res = await attempt(() => uploadPicture(op.id, blob), "Bild gespeichert");
      if (!res) return;
      current = { ...current, picture_at: res.picture_at };
      draw();
      reload();
    };
    input.addEventListener("change", () => upload(input.files[0]));
    drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
    drop.addEventListener("dragleave", () => drop.classList.remove("over"));
    drop.addEventListener("drop", (e) => { e.preventDefault(); drop.classList.remove("over"); upload(e.dataTransfer.files[0]); });
    drop.addEventListener("paste", (e) => {
      const item = [...e.clipboardData.items].find((i) => i.type.startsWith("image/"));
      if (item) { e.preventDefault(); upload(item.getAsFile()); }
    });
    clear(box,
      current.picture_at
        ? pictureImg(current, "picture-large")
        : h("div", { class: "no-picture muted" }, "Noch kein Bild. Der PDB Helper lädt Pic" + op.mdm_number_text + ".png.cam automatisch hoch."),
      h("div", { class: "section" }, drop, input),
      current.picture_at
        ? h("div", { class: "row section" },
          h("span", { class: "muted" }, `Stand ${formatDateTime(current.picture_at)}`),
          h("span", { class: "spacer" }),
          h("button", {
            type: "button",
            class: "small danger",
            onclick: async () => {
              if (!(await confirmDialog("Bild löschen", "Bild dieser Operation löschen?", "Löschen", true))) return;
              if (await attempt(() => deletePicture(op.id), "Bild gelöscht")) { current = { ...current, picture_at: null }; draw(); reload(); }
            },
          }, "Bild löschen"))
        : null);
  };
  draw();
  return { el: box };
}

function pathsTab(op) {
  return {
    el: h("div", {},
      h("p", { class: "muted" }, "Pfade wie sie die PCs sehen. Öffnen und Speichern übernimmt das Mastercam-Add-in."),
      pathsBlock(op.paths)),
  };
}

function revisionsTab(op) {
  const el = api.get(`/operations/${op.id}/revisions`).then((revs) =>
    table({
      columns: [
        { label: "Version", class: "num", value: (r) => String(r.number) },
        { label: "Gespeichert von", value: (r) => r.saved_by },
        { label: "Zeit", value: (r) => formatDateTime(r.saved_at) },
        { label: "Kommentar", value: (r) => r.comment },
        { label: "Archiv", value: (r) => h("code", {}, r.archive_path) },
      ],
      rows: revs,
      empty: "Noch keine Version eingecheckt",
    }));
  return { el };
}

function machineDialog(op, after) {
  const choices = allowedMachines().filter((m) => m.id !== op.machine_id);
  if (!choices.length) {
    modal({ title: "Maschine wechseln", narrow: true, body: h("p", {}, "Keine andere Maschine verfügbar."), actions: [{ label: "OK" }] });
    return;
  }
  const select = h("select", { id: "mc-select" }, choices.map((m) => h("option", { value: m.id }, m.name)));
  modal({
    title: "Maschine wechseln",
    narrow: true,
    body: h("div", {},
      h("label", { class: "field", for: "mc-select" }, "Neue Maschine", select),
      h("p", { class: "muted" },
        "Der PDB Helper verschiebt den Ordner in die neue Maschine. Ist die Datei in Mastercam offen, ",
        "stellt er dort auch die Maschinendefinition um und speichert. Die NC-Programmnummer gehört zur ",
        "alten Maschine und wird zurückgesetzt.")),
    actions: [
      { label: "Abbrechen" },
      {
        label: "Wechseln",
        primary: true,
        onClick: async () => {
          const machineId = Number(select.value);
          const viaHelper = await changeMachineViaHelper(op.id, machineId);
          if (viaHelper) {
            toast(viaHelper.message, viaHelper.status === 200 ? "ok" : "error");
            if (viaHelper.status >= 400 && viaHelper.status !== 500) return false; // nothing changed
            after(); // 200, or 500 = changed in PDB but files need a look
            return;
          }
          const onlyDb = await confirmDialog(
            "PDB Helper nicht erreichbar",
            "Ohne Helper kann nur der Eintrag in der Datenbank umgestellt werden. Der Ordner bleibt bei der alten Maschine und muss von Hand verschoben werden. Trotzdem umstellen?",
            "Nur Datenbank umstellen",
            true,
          );
          if (!onlyDb) return false;
          const res = await attempt(() => api.post(`/operations/${op.id}/machine`, { machine_id: machineId }), "In der Datenbank umgestellt – Ordner von Hand verschieben");
          if (!res) return false;
          after();
        },
      },
    ],
  });
}

async function exportDialog(op) {
  let res;
  try {
    res = await api.post(`/operations/${op.id}/nc-export`);
  } catch (err) {
    modal({ title: "NC-Export", narrow: true, body: h("p", {}, badge("Nicht möglich", "bad"), " ", err.message), actions: [{ label: "OK" }] });
    return;
  }
  modal({
    title: "NC-Export",
    body: h("div", {},
      h("p", {}, badge("Export erlaubt", "ok"), " Programmname ", h("code", {}, res.program_name)),
      h("h2", { class: "section" }, "Kopfzeilen-Werte"),
      h("div", { class: "paths" }, Object.entries(res.header_values).map(([k, v]) =>
        h("div", { class: "kv" }, h("div", { class: "k" }, k), h("div", { class: "v" }, v)))),
      h("h2", { class: "section" }, "Pfade"),
      pathsBlock({ nc_posted: res.paths.nc_posted, nc_export: res.paths.nc_export, nc_info: res.paths.nc_info }),
      h("h2", { class: "section" }, "Info-Datei"),
      h("pre", { class: "paths", style: { background: "var(--code-bg)", padding: "8px", whiteSpace: "pre-wrap" } }, res.info_file_text)),
    actions: [{ label: "Schließen", primary: true }],
  });
}

async function breakLock(op) {
  const ok = await confirmDialog(
    "Sperre aufheben",
    `${op.lock.user} an ${op.lock.pc_name} hat die Datei gesperrt. Ungespeicherte Änderungen dort gehen beim nächsten Speichern verloren. Trotzdem aufheben?`,
    "Aufheben",
    true,
  );
  return ok && attempt(() => api.post(`/admin/break-lock/${op.id}`), "Sperre aufgehoben");
}

async function deleteOperation(op) {
  const ok = await confirmDialog("Operation löschen", `MDM ${op.mdm_number_text} aus der Datenbank löschen? Die Dateien auf dem Laufwerk bleiben erhalten.`, "Löschen", true);
  return ok && attempt(() => api.del(`/operations/${op.id}`), "Gelöscht");
}
