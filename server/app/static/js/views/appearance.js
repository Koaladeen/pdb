// "Mein Profil → Darstellung": every change is shown at once and saved for this user.
import { ACCENTS, DEFAULTS, prefs, setPrefs } from "../prefs.js";
import { clear, h } from "../ui.js";

// One row: label + segmented buttons.
function choice(label, key, options) {
  const group = h("div", { class: "segmented", role: "radiogroup", "aria-label": label },
    options.map(([value, text]) => h("button", {
      type: "button",
      role: "radio",
      class: prefs()[key] === value ? "active" : "",
      "aria-checked": String(prefs()[key] === value),
      onclick: () => setPrefs({ [key]: value }),
    }, text)));
  return h("div", { class: "pref-row" }, h("span", {}, label), group);
}

function accentRow() {
  const current = prefs().accent.toLowerCase();
  const custom = h("input", { type: "color", value: prefs().accent, "aria-label": "Eigene Farbe" });
  custom.addEventListener("input", () => setPrefs({ accent: custom.value }));
  return h("div", { class: "pref-row" }, h("span", {}, "Akzentfarbe"),
    h("div", { class: "swatches" },
      ACCENTS.map(([hex, name]) => h("button", {
        type: "button",
        class: `swatch${hex === current ? " active" : ""}`,
        style: { background: hex },
        title: name,
        "aria-label": name,
        onclick: () => setPrefs({ accent: hex }),
      })),
      h("label", { class: "swatch-custom" }, custom, "eigene")));
}

export function appearancePanel() {
  const el = h("div");
  const draw = () => clear(el,
    choice("Sprache", "language", [["auto", "Automatisch"], ["de", "Deutsch"], ["en", "English"], ["it", "Italiano"], ["lt", "Lietuvių"]]),
    choice("Design", "theme", [["auto", "Automatisch"], ["light", "Hell"], ["dark", "Dunkel"]]),
    accentRow(),
    choice("Grösse", "size", [["s", "Klein"], ["m", "Normal"], ["l", "Gross"], ["xl", "Sehr gross"]]),
    choice("Dichte", "density", [["compact", "Kompakt"], ["normal", "Normal"], ["comfortable", "Luftig"]]),
    choice("Ecken", "corners", [["square", "Eckig"], ["normal", "Normal"], ["round", "Rund"]]),
    choice("Kopfzeile", "header", [["colored", "Farbig"], ["plain", "Schlicht"]]),
    choice("Animationen", "motion", [["off", "Aus"], ["subtle", "Dezent"], ["fancy", "Verspielt"]]),
    choice("Seitenleiste „Geöffnet“", "sidebar", [[true, "Anzeigen"], [false, "Ausblenden"]]),
    choice("Bilder der Operationen", "pictures", [[true, "Anzeigen"], [false, "Ausblenden"]]),
    choice("Startseite", "start", [["search", "Suche"], ["last", "Zuletzt geöffnete Karte"]]),
    h("div", { class: "row section" },
      h("span", { class: "muted", style: { fontSize: "12px" } }, "Wird für deinen Benutzer gespeichert und gilt auf jedem PC."),
      h("span", { class: "spacer" }),
      h("button", { type: "button", onclick: () => setPrefs({ ...DEFAULTS }) }, "Zurücksetzen")));
  window.addEventListener("pdb:prefs", () => { if (el.isConnected) draw(); });
  draw();
  return el;
}
