// Open part cards in the sidebar, like MDM: switch between cards without searching again.
import { load, save } from "./storage.js";
import { customerName } from "./store.js";
import { clear, h } from "./ui.js";

const NAME = "openCards";
const MAX = 30;

export function openCards() {
  return load(NAME, []);
}

function changed(cards) {
  save(NAME, cards);
  window.dispatchEvent(new Event("pdb:cards"));
}

// Called by the card view after loading; keeps the position of an already open card.
export function rememberCard(article) {
  const d = article.data;
  const entry = {
    id: article.id,
    title: d.teilename || `Artikel ${article.id}`,
    subtitle: [d.zeichnungsnummer, d.kunde ? customerName(d.kunde) : null].filter(Boolean).join(" · "),
  };
  const cards = openCards();
  const i = cards.findIndex((c) => c.id === article.id);
  if (i >= 0) cards[i] = entry;
  else cards.push(entry);
  changed(cards.slice(-MAX));
}

export function closeCard(id) {
  const cards = openCards();
  const i = cards.findIndex((c) => c.id === id);
  if (i < 0) return;
  cards.splice(i, 1);
  changed(cards);
  if (currentCardId() === id) {
    const next = cards[i] || cards[i - 1];
    location.hash = next ? `#/article/${next.id}` : "#/search";
  }
}

export function closeAllCards() {
  changed([]);
  if (currentCardId() !== null) location.hash = "#/search";
}

function currentCardId() {
  const m = location.hash.match(/^#\/article\/(\d+)/);
  return m ? Number(m[1]) : null;
}

let listening = null; // AbortController of the current sidebar's listeners

// Sidebar element; redraws itself when cards change or the page changes.
export function sidebar() {
  if (listening) listening.abort();
  listening = new AbortController();
  const opts = { signal: listening.signal };
  const el = h("aside", { class: "sidebar", "aria-label": "Geöffnete Karten" });
  const draw = () => {
    const cards = openCards();
    const current = currentCardId();
    clear(
      el,
      h("div", { class: "sidebar-head" },
        h("span", {}, `Geöffnet (${cards.length})`),
        cards.length ? h("button", { class: "small", title: "Alle Karten schließen", onclick: closeAllCards }, "Alle ×") : null),
      cards.length
        ? h("ul", {}, cards.map((c) =>
          h("li", { class: c.id === current ? "active" : "" },
            h("a", { href: `#/article/${c.id}` },
              h("span", { class: "t" }, c.title),
              c.subtitle ? h("span", { class: "s" }, c.subtitle) : null),
            h("button", { class: "x", title: "Karte schließen", "aria-label": `${c.title} schließen`, onclick: () => closeCard(c.id) }, "×"))))
        : h("p", { class: "muted hint" }, "Geöffnete Artikel erscheinen hier."),
    );
  };
  window.addEventListener("pdb:cards", draw, opts);
  window.addEventListener("hashchange", draw, opts);
  draw();
  return el;
}
