// Quick look at a part card without opening it: picture, main fields, operations with thumbnails.
// Used by the search; "Karte öffnen" (or double-click in the list) opens the real card.
import { api } from "../api.js";
import { displayValue, operationCell } from "../fields.js";
import { pictureImg } from "../pictures.js";
import { store } from "../store.js";
import { badge, clear, h } from "../ui.js";

const cache = new Map(); // article id -> promise of the full article (with operations)

export function loadArticle(id, fresh = false) {
  if (fresh || !cache.has(id)) cache.set(id, api.get(`/articles/${id}`).catch((e) => { cache.delete(id); throw e; }));
  return cache.get(id);
}

export function clearPreviewCache() {
  cache.clear();
}

// Fills `el` with the preview of article `id`. onOpen(id) opens the card, onClose() hides it,
// onPin() (optional) adds it to the user's cards.
export async function showPreview(el, id, { onOpen, onClose, onPin }) {
  clear(el, h("div", { class: "empty" }, "Laden …"));
  let a;
  try {
    a = await loadArticle(id);
  } catch (err) {
    clear(el, h("div", { class: "empty" }, `Fehler: ${err.message}`));
    return;
  }
  if (el.dataset.showing !== String(id)) return; // another row was clicked meanwhile
  const d = a.data;
  const title = [d.zeichnungsnummer, d.teilename].filter(Boolean).join(" ♦ ") || `Artikel ${a.id}`;
  const withPicture = a.operations.find((o) => o.picture_at);

  // main fields: everything filled, in field order (notes last and shortened)
  const rows = store.fields.article
    .filter((fd) => !fd.system && d[fd.key] !== undefined && d[fd.key] !== null && d[fd.key] !== "")
    .map((fd) => {
      let v = displayValue(fd, d[fd.key]);
      if (fd.type === "longtext" && v.length > 160) v = `${v.slice(0, 160)} …`;
      return h("div", { class: "kv" }, h("div", { class: "k" }, fd.label), h("div", { class: "v" }, v));
    });

  const opFields = store.fields.operation.filter((fd) => ["op_number", "prozessbeschreibung", "machine_id", "mdm_number"].includes(fd.key));
  const ops = a.operations.map((op) =>
    h("div", { class: "pv-op" },
      op.picture_at ? h("span", { class: "thumb" }, pictureImg(op)) : h("span", { class: "thumb empty-thumb" }),
      h("div", { class: "pv-op-text" },
        h("div", { class: "pv-op-title" }, opFields.filter((fd) => fd.key !== "mdm_number" && fd.key !== "machine_id").map((fd) => operationCell(fd, op)).join(" · ")),
        h("div", { class: "muted" }, `${op.machine} · MDM ${op.mdm_number_text}`)),
      op.lock ? badge("🔒", op.lock.stale ? "warn" : "") : null));

  // three columns when there is room (picture | data | operations), stacked otherwise
  clear(el,
    h("div", { class: "pv-head" },
      h("strong", {}, title),
      h("div", { class: "row" },
        onPin ? h("button", { type: "button", class: "small", onclick: (e) => { e.stopPropagation(); onPin(); } }, "+ Zu meinen Karten") : null,
        h("button", { class: "small primary", type: "button", onclick: (e) => { e.stopPropagation(); onOpen(a.id); } }, "Karte öffnen"),
        h("button", { class: "close", type: "button", title: "Zuklappen", "aria-label": "Zuklappen", onclick: (e) => { e.stopPropagation(); onClose(); } }, "×"))),
    h("div", { class: "pv-body" },
      withPicture
        ? h("figure", { class: "pv-picture" }, pictureImg(withPicture, "picture-large"))
        : h("div", { class: "no-picture muted pv-picture" }, "Kein Bild"),
      h("div", { class: "pv-fields" }, rows),
      h("div", {},
        h("div", { class: "pv-ops-head" }, `Operationen (${a.operations.length})`),
        h("div", { class: "pv-ops" }, ops.length ? ops : h("div", { class: "muted" }, "Keine Operationen")))));
}
