// Small DOM helpers shared by all views: element builder, modal, toast, tables.
import { language, locale } from "./i18n.js";

// h("button", {class: "primary", onclick: fn}, "Text") -> element
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith("on") && typeof value === "function") el.addEventListener(key.slice(2), value);
    else if (key === "class") el.className = value;
    else if (key === "style" && typeof value === "object") Object.assign(el.style, value);
    else if (key in el && typeof value !== "string") el[key] = value;
    else el.setAttribute(key, value === true ? "" : value);
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
}

export function clear(el, ...children) {
  el.replaceChildren();
  append(el, children);
  return el;
}

export function toast(message, kind = "") {
  const el = h("div", { class: `toast ${kind}` }, message);
  document.getElementById("toasts").append(el);
  setTimeout(() => el.remove(), kind === "error" ? 6000 : 3000);
}

// Runs an async action and shows its error as a toast. Returns the result or undefined.
export async function attempt(fn, okMessage) {
  try {
    const result = await fn();
    if (okMessage) toast(okMessage, "ok");
    return result;
  } catch (err) {
    toast(err.message || String(err), "error");
    return undefined;
  }
}

// modal({title, body, actions: [{label, primary, danger, onClick}]}) -> {close}
// onClick may return false (or a promise of false) to keep the modal open.
const openModals = new Set();

// Called by the router on page change.
export function closeAllModals() {
  for (const close of [...openModals]) close();
}

export function modal({ title, body, actions = [], narrow = false }) {
  const back = h("div", { class: "modal-back" });
  const close = () => {
    back.remove();
    document.removeEventListener("keydown", onKey);
    openModals.delete(close);
  };
  openModals.add(close);
  // Escape closes only the top-most dialog.
  const onKey = (e) => { if (e.key === "Escape" && [...openModals].at(-1) === close) close(); };
  document.addEventListener("keydown", onKey);
  const buttons = actions.map((a) => {
    const btn = h("button", { class: [a.primary && "primary", a.danger && "danger"].filter(Boolean).join(" ") }, a.label);
    btn.addEventListener("click", async () => {
      btn.disabled = true;
      try {
        const keep = a.onClick ? await a.onClick() : undefined;
        if (keep !== false) close();
      } finally {
        btn.disabled = false;
      }
    });
    return btn;
  });
  const box = h(
    "div",
    { class: `modal${narrow ? " narrow" : ""}`, role: "dialog", "aria-modal": "true" },
    h("header", {}, h("h2", {}, title), h("button", { class: "close", "aria-label": "Schließen", onclick: close }, "×")),
    h("div", { class: "body" }, body),
    buttons.length ? h("footer", {}, buttons) : null,
  );
  back.append(box);
  back.addEventListener("mousedown", (e) => { if (e.target === back) close(); });
  document.body.append(back);
  const first = box.querySelector("input, select, textarea");
  if (first) first.focus();
  return { close, box };
}

export function confirmDialog(title, text, label = "OK", danger = false) {
  return new Promise((resolve) => {
    let answered = false;
    const m = modal({
      title,
      narrow: true,
      body: h("p", {}, text),
      actions: [
        { label: "Abbrechen", onClick: () => { answered = true; resolve(false); } },
        { label, primary: !danger, danger, onClick: () => { answered = true; resolve(true); } },
      ],
    });
    const observer = new MutationObserver(() => {
      if (!m.box.isConnected) { observer.disconnect(); if (!answered) resolve(false); }
    });
    observer.observe(document.body, { childList: true });
  });
}

// table({columns: [{label, value: row => node|string, class}], rows, onRowClick, empty})
export function table({ columns, rows, onRowClick, empty = "Keine Einträge" }) {
  if (!rows.length) return h("div", { class: "empty" }, empty);
  return h(
    "div",
    { class: "table-wrap" },
    h(
      "table",
      {},
      h("thead", {}, h("tr", {}, columns.map((c) => h("th", { class: c.class }, c.label)))),
      h(
        "tbody",
        {},
        rows.map((row) =>
          h(
            "tr",
            { class: onRowClick ? "clickable" : "", onclick: onRowClick ? () => onRowClick(row) : null },
            columns.map((c) => h("td", { class: c.class }, c.value(row))),
          ),
        ),
      ),
    ),
  );
}

export function badge(text, kind = "") {
  return h("span", { class: `badge ${kind}` }, text);
}

export function formatDateTime(iso) {
  if (!iso) return "";
  // Server times are UTC without zone marker.
  const d = new Date(iso.endsWith("Z") ? iso : `${iso}Z`);
  return d.toLocaleString(locale(), { dateStyle: "short", timeStyle: "short" });
}

export function formatDate(isoDate) {
  if (!isoDate) return "";
  const [y, m, d] = isoDate.split("-");
  if (language() === "de") return `${d}.${m}.${y}`;
  return new Date(Date.UTC(Number(y), Number(m) - 1, Number(d))).toLocaleDateString(locale(), { timeZone: "UTC" });
}

export function debounce(fn, ms = 250) {
  let t;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}
