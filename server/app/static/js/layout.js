// Per-user arrangement of a page's big blocks (order, width, hidden), saved with the user's
// preferences on the server. "Layout anpassen" switches into an edit mode: drag a block by its
// bar (or move it with ‹ ›), pick a width, hide/show; "Fertig" saves, "Abbrechen" discards,
// "Standard" goes back to the default arrangement.
//
//   const { el, button } = blockLayout("article", [
//     { id: "fields", title: "Kartenfelder", span: 4, el: fieldsPanel },
//     ...
//   ]);
import { prefs, setPrefs } from "./prefs.js";
import { clear, h, toast } from "./ui.js";

// of 6 grid columns
const SPANS = [[2, "⅓"], [3, "½"], [4, "⅔"], [6, "Ganze Breite"]];

// Saved order first (known blocks only), new blocks the page got since then at their default place.
function arranged(page, blocks) {
  const saved = (prefs().layouts || {})[page] || [];
  const known = new Map(blocks.map((b) => [b.id, b]));
  const out = saved.filter((b) => known.has(b.id)).map((b) => ({ id: b.id, span: b.span || known.get(b.id).span, hidden: !!b.hidden }));
  blocks.forEach((b, i) => {
    if (!out.some((x) => x.id === b.id)) out.splice(Math.min(i, out.length), 0, { id: b.id, span: b.span, hidden: false });
  });
  return out;
}

const defaults = (blocks) => blocks.map((b) => ({ id: b.id, span: b.span, hidden: false }));

export function blockLayout(page, blocks) {
  const byId = new Map(blocks.map((b) => [b.id, b]));
  let order = arranged(page, blocks);
  let before = null; // order when edit mode started (for "Abbrechen")
  let editing = false;
  let dragId = null;

  const grid = h("div", { class: "block-grid" });
  const bar = h("div", { class: "layout-bar" });
  const button = h("button", { type: "button", title: "Reihenfolge, Breite und sichtbare Blöcke dieser Seite für deinen Benutzer festlegen", onclick: () => start() }, "Layout anpassen");

  const start = () => { before = order.map((b) => ({ ...b })); editing = true; draw(); };
  const stop = () => { editing = false; draw(); };
  const save = () => {
    setPrefs({ layouts: { ...(prefs().layouts || {}), [page]: order } });
    toast("Layout gespeichert", "ok");
    stop();
  };
  const move = (id, to) => {
    const from = order.findIndex((b) => b.id === id);
    if (from < 0 || to < 0 || to >= order.length || from === to) return;
    const [b] = order.splice(from, 1);
    order.splice(to, 0, b);
    draw();
  };

  const tools = (b, i) => h("div", { class: "block-tools" },
    h("span", { class: "block-handle", title: "Ziehen zum Verschieben", "aria-hidden": "true" }, "⠿"),
    h("strong", {}, byId.get(b.id).title),
    h("span", { class: "spacer" }),
    h("button", { type: "button", class: "icon", title: "Nach vorne", "aria-label": "Nach vorne", disabled: i === 0, onclick: () => move(b.id, i - 1) }, "‹"),
    h("button", { type: "button", class: "icon", title: "Nach hinten", "aria-label": "Nach hinten", disabled: i === order.length - 1, onclick: () => move(b.id, i + 1) }, "›"),
    h("select", { "aria-label": "Breite", onchange: (e) => { b.span = Number(e.target.value); draw(); } },
      SPANS.map(([v, label]) => h("option", { value: v, selected: b.span === v }, label))),
    h("button", { type: "button", onclick: () => { b.hidden = !b.hidden; draw(); } }, b.hidden ? "Einblenden" : "Ausblenden"));

  const wrap = (b, i) => {
    const box = h("div", { class: `block span-${b.span}${b.hidden ? " is-hidden" : ""}`, "data-id": b.id }, editing ? tools(b, i) : null, byId.get(b.id).el);
    if (!editing) return box;
    box.draggable = true;
    box.addEventListener("dragstart", (e) => { dragId = b.id; e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", b.id); box.classList.add("dragging"); });
    box.addEventListener("dragend", () => { dragId = null; box.classList.remove("dragging"); grid.querySelectorAll(".drop-before, .drop-after").forEach((x) => x.classList.remove("drop-before", "drop-after")); });
    const after = (e) => { const r = box.getBoundingClientRect(); return r.width > r.height * 1.5 ? e.clientY > r.top + r.height / 2 : e.clientX > r.left + r.width / 2; };
    box.addEventListener("dragover", (e) => {
      if (!dragId || dragId === b.id) return;
      e.preventDefault();
      const a = after(e);
      box.classList.toggle("drop-after", a);
      box.classList.toggle("drop-before", !a);
    });
    box.addEventListener("dragleave", () => box.classList.remove("drop-before", "drop-after"));
    box.addEventListener("drop", (e) => {
      if (!dragId || dragId === b.id) return;
      e.preventDefault();
      const target = order.findIndex((x) => x.id === b.id) + (after(e) ? 1 : 0);
      const from = order.findIndex((x) => x.id === dragId);
      move(dragId, from < target ? target - 1 : target);
    });
    return box;
  };

  const draw = () => {
    grid.classList.toggle("editing", editing);
    button.disabled = editing;
    clear(grid, order.map((b, i) => (editing || !b.hidden ? wrap(b, i) : null)));
    clear(bar, editing ? h("div", { class: "panel panel-body layout-edit-bar" },
      h("span", {}, "Blöcke ziehen oder mit ‹ › verschieben, Breite wählen, ausblenden. Gilt für deinen Benutzer auf jedem PC."),
      h("span", { class: "spacer" }),
      h("button", { type: "button", onclick: () => { order = defaults(blocks); draw(); } }, "Standard"),
      h("button", { type: "button", onclick: () => { order = before; stop(); } }, "Abbrechen"),
      h("button", { type: "button", class: "primary", onclick: save }, "Fertig")) : null);
  };
  draw();
  return { el: h("div", { class: "block-layout" }, bar, grid), button };
}
