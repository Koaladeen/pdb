// Admin: configurable fields. Toggles save immediately; system fields cannot be switched off.
import { api } from "../../api.js";
import { refreshCatalog } from "../../store.js";
import { attempt, badge, clear, confirmDialog, h, modal, table } from "../../ui.js";

const TYPES = {
  text: "Text",
  longtext: "Langtext",
  suggest: "Text mit Vorschlägen",
  choice: "Auswahlliste",
  integer: "Ganzzahl",
  number: "Zahl",
  date: "Datum",
  duration: "Dauer",
  user: "Benutzer",
  customer: "Kunde (Kundenliste)",
  boolean: "Ja/Nein",
};
const ENTITY_LABEL = { article: "Artikel (Karte)", operation: "Operation (Prozessdaten)" };

let entity = "article";

export async function render(root) {
  const fields = await api.get(`/admin/fields?entity=${entity}`);
  const reload = async () => { await refreshCatalog(); render(root); };

  const toggle = (fd, key, disabled = false) => {
    const cb = h("input", { type: "checkbox", checked: fd[key], disabled, "aria-label": key });
    cb.addEventListener("click", (e) => e.stopPropagation());
    cb.addEventListener("change", async () => {
      const ok = await attempt(() => api.patch(`/admin/fields/${fd.id}`, { [key]: cb.checked }));
      if (!ok) { cb.checked = !cb.checked; return; }
      fd[key] = cb.checked;
      refreshCatalog();
    });
    return cb;
  };

  clear(
    root,
    h("div", { class: "page-head" },
      h("h1", {}, "Felder"),
      h("button", { class: "primary", onclick: () => fieldDialog(null, reload) }, "+ Neues Feld")),
    h("div", { class: "tabs" }, Object.entries(ENTITY_LABEL).map(([key, label]) =>
      h("button", { class: key === entity ? "active" : "", onclick: () => { entity = key; render(root); } }, label))),
    h("p", { class: "muted" }, "Ausgeschaltete Felder verschwinden aus Formularen, Listen und Suche; gespeicherte Werte bleiben erhalten. Zeile anklicken für Bezeichnung, Reihenfolge und Auswahlwerte."),
    h("div", { class: "panel" }, table({
      columns: [
        { label: "Reihenfolge", class: "num", value: (fd) => String(fd.sort_order) },
        { label: "Bezeichnung", value: (fd) => [fd.label, fd.system ? [" ", badge("System")] : null] },
        { label: "Schlüssel", value: (fd) => h("code", {}, fd.key) },
        { label: "Typ", value: (fd) => TYPES[fd.type] || fd.type },
        { label: "Aktiv", value: (fd) => toggle(fd, "enabled", fd.system) },
        { label: "Pflicht", value: (fd) => toggle(fd, "required", fd.system) },
        { label: "Suche", value: (fd) => toggle(fd, "searchable") },
        { label: "In Liste", value: (fd) => toggle(fd, "show_in_list") },
        { label: "Platzhalter", value: (fd) => (fd.placeholder ? h("code", {}, `#${fd.placeholder}`) : "") },
        { label: "Werte", class: "num", value: (fd) => (["choice", "suggest"].includes(fd.type) ? String(fd.choices.filter((c) => c.active).length) : "") },
      ],
      rows: fields,
      onRowClick: (fd) => fieldDialog(fd, reload),
    })),
  );
}

function fieldDialog(fd, reload) {
  const isNew = !fd;
  const label = h("input", { id: "fd-label", value: fd?.label ?? "" });
  const key = h("input", { id: "fd-key", value: fd?.key ?? "", disabled: !isNew, placeholder: "z. B. oberflaeche" });
  const type = h("select", { id: "fd-type", disabled: fd?.system }, Object.entries(TYPES).map(([k, v]) => h("option", { value: k, selected: (fd?.type ?? "text") === k }, v)));
  const order = h("input", { id: "fd-order", type: "number", value: fd?.sort_order ?? 1000 });
  const placeholder = h("input", { id: "fd-ph", value: fd?.placeholder ?? "", placeholder: "z. B. PDB_Customer" });
  if (isNew) {
    label.addEventListener("input", () => {
      key.value = label.value.toLowerCase()
        .replace(/ä/g, "ae").replace(/ö/g, "oe").replace(/ü/g, "ue").replace(/ß/g, "ss")
        .replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").replace(/^(\d)/, "f_$1");
    });
  }

  const choicesBox = h("div");
  const renderChoices = () => {
    if (isNew || !["choice", "suggest"].includes(fd.type)) return clear(choicesBox);
    const newValue = h("input", { placeholder: "Neuer Wert" });
    const blocks = h("input", { type: "checkbox" });
    clear(choicesBox,
      h("h2", { class: "section" }, "Auswahlwerte"),
      table({
        columns: [
          { label: "Wert", value: (c) => c.value },
          { label: "Aktiv", value: (c) => choiceToggle(c, "active") },
          { label: "Sperrt NC-Export", value: (c) => choiceToggle(c, "blocks_export") },
        ],
        rows: fd.choices,
        empty: "Noch keine Werte",
      }),
      h("div", { class: "row section" },
        h("div", { style: { flex: "1" } }, newValue),
        fd.type === "choice" ? h("label", { class: "check" }, blocks, "sperrt Export") : null,
        h("button", {
          type: "button",
          onclick: async () => {
            if (!newValue.value.trim()) return;
            const c = await attempt(() => api.post(`/admin/fields/${fd.id}/choices`, { value: newValue.value.trim(), sort_order: fd.choices.length, blocks_export: blocks.checked }));
            if (c) { fd.choices = [...fd.choices.filter((x) => x.id !== c.id), c]; renderChoices(); refreshCatalog(); }
          },
        }, "Hinzufügen")));
  };
  const choiceToggle = (c, prop) => {
    const cb = h("input", { type: "checkbox", checked: c[prop] });
    cb.addEventListener("change", async () => {
      const ok = await attempt(() => api.patch(`/admin/choices/${c.id}`, { [prop]: cb.checked }));
      if (ok) { c[prop] = cb.checked; refreshCatalog(); } else cb.checked = !cb.checked;
    });
    return cb;
  };
  renderChoices();

  const body = h("div", {},
    fd?.system ? h("p", { class: "muted" }, "Systemfeld: wird für Sperren, Export oder Pfade gebraucht und kann nicht ausgeschaltet werden.") : null,
    h("div", { class: "form-grid" },
      h("label", { class: "field", for: "fd-label" }, "Bezeichnung", label),
      h("label", { class: "field", for: "fd-key" }, "Schlüssel (nicht änderbar)", key),
      h("label", { class: "field", for: "fd-type" }, "Typ", type),
      h("label", { class: "field", for: "fd-order" }, "Reihenfolge", order),
      h("label", { class: "field wide", for: "fd-ph" }, "Platzhalter im NC-Kopf (ohne #, optional)", placeholder)),
    choicesBox);

  modal({
    title: isNew ? `Neues Feld – ${ENTITY_LABEL[entity]}` : fd.label,
    body,
    actions: [
      !isNew && !fd.system ? {
        label: "Löschen",
        danger: true,
        onClick: async () => {
          if (!(await confirmDialog("Feld löschen", `Feld „${fd.label}“ löschen? Gespeicherte Werte bleiben erhalten und kommen zurück, wenn das Feld neu angelegt wird.`, "Löschen", true))) return false;
          if (!(await attempt(() => api.del(`/admin/fields/${fd.id}`)))) return false;
          reload();
        },
      } : null,
      { label: "Abbrechen", onClick: () => { reload(); } },
      {
        label: "Speichern",
        primary: true,
        onClick: async () => {
          const payload = { label: label.value.trim(), sort_order: Number(order.value), placeholder: placeholder.value.trim() || null };
          if (!fd?.system) payload.type = type.value;
          const ok = await attempt(
            () => (isNew ? api.post("/admin/fields", { ...payload, entity, key: key.value.trim() }) : api.patch(`/admin/fields/${fd.id}`, payload)),
            "Gespeichert",
          );
          if (!ok) return false;
          reload();
        },
      },
    ].filter(Boolean),
  });
}
