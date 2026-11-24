// Search panel (like MDM's left-hand search) and result list.
import { api, qs } from "../api.js";
import { displayValue, fieldForm } from "../fields.js";
import { openInMastercam } from "../helper.js";
import { pictureImg } from "../pictures.js";
import { clearSearches, forgetSearch, recentSearches, recentTerms, rememberSearch } from "../recent.js";
import { openCards, rememberCard } from "../workspace.js";
import { loadArticle, showPreview } from "./preview.js";
import { customerName, machineName, store, userName } from "../store.js";
import { attempt, badge, clear, h, modal, table, toast } from "../ui.js";

// Kept while the page is open, so going back to the search shows the last result.
const state = { filters: {}, offset: 0 };
const PAGE = 50;

// Filter inputs: fixed ones first, then every enabled searchable field.
function filterDefs() {
  const fixed = [
    { name: "q", label: "Freitext" },
    { name: "mdm_number", label: "MDM-Nummer", type: "number" },
    { name: "nc_number", label: "NC-Programmnummer", type: "number" },
    { name: "machine_id", label: "Maschine", type: "machine" },
  ];
  const dynamic = [...store.fields.article, ...store.fields.operation]
    .filter((fd) => fd.searchable && !fd.system && fd.type !== "longtext")
    .map((fd) => ({ name: `${fd.entity}.${fd.key}`, label: fd.label, fd }));
  return [...fixed, ...dynamic];
}

// "Kunde: Muster AG · Maschine: DMU 50" for a stored filter set.
function describe(filters, defs) {
  return Object.entries(filters).map(([name, value]) => {
    const def = defs.find((d) => d.name === name);
    let shown = value;
    if (def?.type === "machine") shown = machineName(Number(value));
    else if (def?.fd?.type === "user") shown = userName(Number(value));
    else if (def?.fd?.type === "customer") shown = customerName(Number(value));
    return name === "q" ? `„${shown}“` : `${def ? def.label : name}: ${shown}`;
  }).join(" · ");
}

function recentPanel(root, defs) {
  const list = recentSearches();
  if (!list.length) return null;
  return h("div", { class: "recent" },
    h("div", { class: "recent-head" },
      h("span", {}, "Letzte Suchen"),
      h("button", { class: "linklike", type: "button", onclick: () => { clearSearches(); render(root); } }, "Verlauf löschen")),
    list.map((r, i) => {
      const text = describe(r.filters, defs);
      return h("div", { class: "recent-item" },
        h("button", { class: "go", type: "button", title: text, onclick: () => { state.filters = { ...r.filters }; state.offset = 0; rememberSearch(r.filters); render(root); } }, text),
        h("button", { class: "x", type: "button", title: "Aus Verlauf entfernen", "aria-label": "Aus Verlauf entfernen", onclick: () => { forgetSearch(i); render(root); } }, "×"));
    }));
}

function filterInput(def) {
  const value = state.filters[def.name] ?? "";
  if (def.name === "q") {
    return [
      h("input", { name: "q", value, list: "recent-terms", autocomplete: "off", placeholder: "Name, Nummer, Kunde …" }),
      h("datalist", { id: "recent-terms" }, recentTerms().map((t) => h("option", { value: t }))),
    ];
  }
  if (def.type === "machine") {
    return h("select", { name: def.name }, h("option", { value: "" }, "Alle"),
      store.machines.map((m) => h("option", { value: m.id, selected: String(m.id) === value }, m.name)));
  }
  if (def.fd && def.fd.type === "choice") {
    return h("select", { name: def.name }, h("option", { value: "" }, "Alle"),
      def.fd.choices.map((c) => h("option", { value: c.value, selected: c.value === value }, c.value)));
  }
  if (def.fd && def.fd.type === "customer") {
    return h("select", { name: def.name }, h("option", { value: "" }, "Alle"),
      store.customers.map((c) => h("option", { value: c.id, selected: String(c.id) === value }, c.active ? c.name : `${c.name} (inaktiv)`)));
  }
  if (def.fd && def.fd.type === "user") {
    return h("select", { name: def.name }, h("option", { value: "" }, "Alle"),
      store.users.map((u) => h("option", { value: u.id, selected: String(u.id) === value }, u.display_name)));
  }
  return h("input", { name: def.name, type: def.type === "number" ? "number" : "text", value });
}

export async function render(root) {
  const results = h("div", { class: "panel" });
  const form = h("form", { class: "panel-body", style: { display: "flex", flexDirection: "column", gap: "10px" } });
  const defs = filterDefs();
  for (const def of defs) {
    form.append(h("label", { class: "field" }, def.label, filterInput(def)));
  }
  form.append(
    h("div", { class: "row" },
      h("button", { class: "primary", type: "submit" }, "Suche starten"),
      h("button", { type: "button", onclick: () => { state.filters = {}; state.offset = 0; render(root); } }, "Leeren")),
  );
  form.addEventListener("submit", (e) => {
    e.preventDefault();
    state.filters = Object.fromEntries([...new FormData(form).entries()].map(([k, v]) => [k, String(v).trim()]).filter(([, v]) => v !== ""));
    state.offset = 0;
    rememberSearch(state.filters);
    recentSlot.replaceChildren(recentPanel(root, defs) || "");
    load(results);
  });

  const recentSlot = h("div", {}, recentPanel(root, defs));
  clear(
    root,
    h("div", { class: "page-head" }, h("h1", {}, "Artikel"), h("button", { class: "primary", onclick: newArticle }, "+ Neuer Artikel")),
    h("div", { class: "split" }, h("div", { class: "panel" }, h("div", { class: "panel-title" }, "Suche"), recentSlot, form), results),
  );
  await load(results);
}

async function load(results) {
  clear(results, h("div", { class: "empty" }, "Suche …"));
  const data = await attempt(() => api.get(`/articles${qs({ ...state.filters, limit: PAGE, offset: state.offset })}`));
  if (!data) return clear(results, h("div", { class: "empty" }, "Suche fehlgeschlagen"));

  const listFields = store.fields.article.filter((fd) => fd.show_in_list && !fd.system);
  const columns = [
    {
      label: "",
      value: (a) => {
        const op = a.operations.find((o) => o.picture_at);
        return op ? h("span", { class: "thumb" }, pictureImg(op)) : "";
      },
    },
    ...listFields.map((fd) => ({ label: fd.label, value: (a) => displayValue(fd, a.data[fd.key]) })),
    { label: "Operationen", class: "num", value: (a) => String(a.operations.length) },
    { label: "Maschinen", value: (a) => [...new Set(a.operations.map((o) => o.machine))].join(", ") },
    {
      label: "",
      value: (a) => {
        const locked = a.operations.filter((o) => o.lock);
        return locked.length ? badge(`🔒 ${locked.length}`, locked.some((o) => o.lock.stale) ? "warn" : "") : "";
      },
    },
  ];
  const pages = Math.ceil(data.total / PAGE);
  const page = Math.floor(state.offset / PAGE) + 1;

  // Click = expand the row in place (picture, data, operations), click again = collapse.
  // Double-click = add to "my cards" (sidebar) and stay here. Enter / "Karte öffnen" = open.
  const listPanel = h("div", { class: "panel" });
  const layout = h("div", { class: "results-layout" }, listPanel);
  const open = (id) => { location.hash = `#/article/${id}`; };
  const mine = new Set(openCards().map((c) => c.id));
  let rowsEls = [];
  let detailRow = null;

  const collapse = () => {
    detailRow?.remove();
    detailRow = null;
    rowsEls.forEach((tr) => tr.classList.remove("selected"));
    state.selected = null;
  };
  const expand = (i) => {
    if (i < 0 || i >= data.items.length) return;
    const id = data.items[i].id;
    collapse();
    state.selected = id;
    rowsEls[i].classList.add("selected");
    const box = h("div", { class: "preview-inline", "data-showing": String(id) });
    detailRow = h("tr", { class: "detail-row" }, h("td", { colspan: String(columns.length) }, box));
    rowsEls[i].after(detailRow);
    rowsEls[i].scrollIntoView({ block: "nearest" });
    showPreview(box, id, { onOpen: open, onClose: collapse, onPin: () => pin(i) });
  };
  const toggle = (i) => (state.selected === data.items[i].id ? collapse() : expand(i));
  const pin = async (i) => {
    const id = data.items[i].id;
    const a = await attempt(() => loadArticle(id));
    if (!a) return;
    rememberCard(a);
    mine.add(id);
    rowsEls[i].classList.add("pinned");
    toast(`„${a.data.teilename || `Artikel ${id}`}“ zu deinen Karten hinzugefügt`, "ok");
  };

  const tableEl = table({ columns, rows: data.items, onRowClick: () => {}, empty: "Keine Artikel gefunden" });
  rowsEls = [...tableEl.querySelectorAll("tbody tr")];
  rowsEls.forEach((tr, i) => {
    tr.tabIndex = 0;
    tr.title = "Klick: Infos aufklappen · Doppelklick: zu meinen Karten · Enter: Karte öffnen";
    if (mine.has(data.items[i].id)) tr.classList.add("pinned");
    // a double-click also fires two clicks: wait briefly so it does not expand+collapse
    let clickTimer = null;
    tr.addEventListener("click", () => {
      clearTimeout(clickTimer);
      clickTimer = setTimeout(() => toggle(i), 220);
    });
    tr.addEventListener("dblclick", () => {
      clearTimeout(clickTimer);
      pin(i);
    });
    tr.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown") { e.preventDefault(); if (state.selected) expand(i + 1); rowsEls[i + 1]?.focus(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); if (state.selected) expand(i - 1); rowsEls[i - 1]?.focus(); }
      else if (e.key === " ") { e.preventDefault(); toggle(i); }
      else if (e.key === "Enter") open(data.items[i].id);
      else if (e.key === "Escape") collapse();
    });
  });

  clear(
    listPanel,
    h("div", { class: "panel-title" }, `Treffer: ${data.total}`,
      pages > 1
        ? h("span", { class: "row" },
          h("button", { class: "small", disabled: page <= 1, onclick: () => { state.offset -= PAGE; load(results); } }, "‹"),
          `${page} / ${pages}`,
          h("button", { class: "small", disabled: page >= pages, onclick: () => { state.offset += PAGE; load(results); } }, "›"))
        : null),
    tableEl,
  );
  clear(results, layout);
  // coming back to the search: the last expanded row is expanded again
  const again = data.items.findIndex((a) => a.id === state.selected);
  if (again >= 0) expand(again);
}

// New card, optionally with its first operation - which can go straight into Mastercam
// (the helper then creates the new file with the machine's definition in its folder).
function newArticle() {
  const form = fieldForm("article");
  const machines = store.machines.filter((m) => m.allowed);
  const machine = h("select", { id: "na-machine" },
    h("option", { value: "" }, "– später –"),
    machines.map((m) => h("option", { value: m.id }, m.name)));
  const process = h("input", { id: "na-process", value: "1 OP" });
  const openNow = h("input", { type: "checkbox", checked: true });
  const sync = () => { process.disabled = openNow.disabled = !machine.value; };
  machine.addEventListener("change", sync);
  sync();
  modal({
    title: "Neuer Artikel",
    body: h("div", {},
      form.el,
      h("h2", { class: "section" }, "Erste Operation (optional)"),
      h("div", { class: "form-grid" },
        h("label", { class: "field", for: "na-machine" }, "Maschine", machine),
        h("label", { class: "field", for: "na-process" }, "Prozessbeschreibung", process),
        h("label", { class: "check wide" }, openNow, "Gleich in Mastercam öffnen – neue Datei mit der Maschinendefinition wird angelegt"))),
    actions: [
      { label: "Abbrechen" },
      {
        label: "Anlegen",
        primary: true,
        onClick: async () => {
          const a = await attempt(() => api.post("/articles", { data: form.values() }), "Artikel angelegt");
          if (!a) return false;
          if (machine.value) {
            const data = process.value.trim() ? { prozessbeschreibung: process.value.trim() } : {};
            const op = await attempt(() => api.post(`/articles/${a.id}/operations`, { machine_id: Number(machine.value), data }));
            if (op && openNow.checked) openInMastercam(op.id);
          }
          location.hash = `#/article/${a.id}`;
        },
      },
    ],
  });
}
