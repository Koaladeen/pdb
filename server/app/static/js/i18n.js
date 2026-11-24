// Interface language. The UI is written in German; for other languages every text that appears
// on the page (text, title, placeholder, aria-label, alt) is looked up in that language's
// dictionary (js/i18n/<lang>.js) and replaced. So screens need no translation code at all:
// write the German text, add it to the dictionary. Texts with values use {0} patterns.
// Not translated: what users type (inputs, textareas), code/paths (<code>, <pre>), and anything
// inside [data-no-i18n].
import * as en from "./i18n/en.js";
import * as it from "./i18n/it.js";
import * as lt from "./i18n/lt.js";

const DICTS = {
  en: { texts: en.TEXTS, patterns: compile(en.PATTERNS) },
  it: { texts: it.TEXTS, patterns: compile(it.PATTERNS) },
  lt: { texts: lt.TEXTS, patterns: compile(lt.PATTERNS) },
};
const LOCALES = { de: "de-CH", en: "en-GB", it: "it-CH", lt: "lt-LT" };
const ATTRS = ["title", "placeholder", "aria-label", "alt"];
const SKIP = "textarea, code, pre, script, style, [data-no-i18n]";

let lang = "de";
let observer = null;

function compile(patterns) {
  return patterns.map(([de, en]) => {
    const parts = de.split(/\{\d\}/).map((p) => p.replace(/[.*+?^$()|[\]\\]/g, "\\$&"));
    return { re: new RegExp(`^${parts.join("(.+?)")}$`, "s"), en };
  });
}

export function language() {
  return lang;
}

// For dates and numbers.
export function locale() {
  return LOCALES[lang] || "de-CH";
}

// pref: "auto" (browser language), "de", "en", "it" or "lt". Returns true if a reload is needed (back to German).
export function setLanguage(pref) {
  const browser = (navigator.language || "de").slice(0, 2).toLowerCase();
  const next = LOCALES[pref] ? pref : LOCALES[browser] ? browser : "en";
  const wasTranslating = lang !== "de";
  if (wasTranslating && next !== lang) { lang = next; return true; } // e.g. en -> it: reload from German
  lang = next;
  document.documentElement.lang = lang;
  if (lang === "de") {
    observer?.disconnect();
    observer = null;
    return wasTranslating; // the page holds English texts: reload to get the German ones back
  }
  translateTree(document.body);
  document.title = t(document.title);
  if (!observer) {
    observer = new MutationObserver((records) => {
      for (const r of records) {
        if (r.type === "childList") r.addedNodes.forEach((n) => translateTree(n));
        else if (r.type === "characterData") translateNode(r.target);
        else if (r.type === "attributes") translateAttr(r.target, r.attributeName);
      }
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ATTRS });
  }
  return false;
}

// Translate one text (also usable directly, e.g. for confirm texts built in code).
export function t(text) {
  if (lang === "de" || !text) return text;
  const dict = DICTS[lang];
  if (!dict) return text;
  return text.split("\n").map((line) => translateLine(line, dict)).join("\n");
}

function translateLine(line, dict) {
  const core = line.trim();
  if (!core) return line;
  let out = dict.texts[core];
  if (out === undefined) {
    for (const p of dict.patterns) {
      const m = p.re.exec(core);
      if (m) {
        out = p.en.replace(/\{(\d)\}/g, (_, i) => translateLine(m[Number(i) + 1], dict));
        break;
      }
    }
  }
  if (out === undefined) return line;
  const lead = line.slice(0, line.indexOf(core));
  const trail = line.slice(line.indexOf(core) + core.length);
  return lead + out + trail;
}

function skipped(el) {
  return el && el.closest && el.closest(SKIP);
}

function translateNode(node) {
  if (node.nodeType !== Node.TEXT_NODE || skipped(node.parentElement)) return;
  const next = t(node.nodeValue);
  if (next !== node.nodeValue) node.nodeValue = next;
}

function translateAttr(el, name) {
  if (skipped(el)) return;
  const v = el.getAttribute(name);
  if (!v) return;
  const next = t(v);
  if (next !== v) el.setAttribute(name, next);
}

function translateTree(root) {
  if (!root) return;
  if (root.nodeType === Node.TEXT_NODE) { translateNode(root); return; }
  if (root.nodeType !== Node.ELEMENT_NODE || skipped(root)) return;
  ATTRS.forEach((a) => root.hasAttribute(a) && translateAttr(root, a));
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
    acceptNode: (n) => (n.nodeType === Node.ELEMENT_NODE && n.matches(SKIP) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
  });
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    if (n.nodeType === Node.TEXT_NODE) translateNode(n);
    else ATTRS.forEach((a) => n.hasAttribute(a) && translateAttr(n, a));
  }
}
