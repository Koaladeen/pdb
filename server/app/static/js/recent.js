// Recent searches: the last filter sets the user ran, newest first.
import { load, save } from "./storage.js";

const NAME = "recentSearches";
const MAX = 10;

export function recentSearches() {
  return load(NAME, []);
}

export function rememberSearch(filters) {
  if (!Object.keys(filters).length) return;
  const sig = JSON.stringify(sorted(filters));
  const list = recentSearches().filter((r) => JSON.stringify(sorted(r.filters)) !== sig);
  list.unshift({ filters, at: Date.now() });
  save(NAME, list.slice(0, MAX));
}

export function forgetSearch(index) {
  const list = recentSearches();
  list.splice(index, 1);
  save(NAME, list);
}

export function clearSearches() {
  save(NAME, []);
}

// Earlier free-text terms, for the suggestion list of the search field.
export function recentTerms() {
  return [...new Set(recentSearches().map((r) => r.filters.q).filter(Boolean))];
}

function sorted(obj) {
  return Object.fromEntries(Object.entries(obj).sort(([a], [b]) => a.localeCompare(b)));
}
