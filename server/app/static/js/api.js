// Thin wrapper around fetch: adds the login token, turns error responses into readable messages.

const TOKEN_KEY = "pdb.token";

function readToken() {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}

let token = readToken();

export function setToken(value) {
  token = value;
  try {
    if (value) localStorage.setItem(TOKEN_KEY, value);
    else localStorage.removeItem(TOKEN_KEY);
  } catch { /* private mode: token lives only in memory */ }
}

export function hasToken() {
  return Boolean(token);
}

// Handed to the PDB Helper on this PC so it works as the same user without its own login.
export function currentToken() {
  return token;
}

export class ApiError extends Error {
  constructor(status, detail) {
    super(formatDetail(detail));
    this.status = status;
    this.detail = detail;
  }
}

function formatDetail(detail) {
  if (detail == null) return "Unbekannter Fehler";
  if (typeof detail === "string") return detail;
  if (Array.isArray(detail)) {
    // FastAPI validation errors
    return detail.map((d) => `${(d.loc || []).slice(1).join(".")}: ${d.msg}`).join("; ");
  }
  if (detail.message) return detail.message;
  return JSON.stringify(detail);
}

// Called when the login has expired; app.js sets it to show the login page.
export let onUnauthorized = () => {};
export function setUnauthorizedHandler(fn) {
  onUnauthorized = fn;
}

async function request(method, path, body) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  let data = null;
  const text = await res.text();
  if (text) {
    try { data = JSON.parse(text); } catch { data = text; }
  }
  if (res.status === 401 && path !== "/auth/login") {
    setToken(null);
    onUnauthorized();
  }
  if (!res.ok) throw new ApiError(res.status, data && data.detail !== undefined ? data.detail : data);
  return data;
}

// Binary requests (pictures). <img src> cannot send the login header, so pictures are
// fetched here and shown via object URLs.
async function binary(method, path, body) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  const res = await fetch(path, { method, headers, body });
  if (res.status === 401) { setToken(null); onUnauthorized(); }
  if (!res.ok) {
    let detail = null;
    try { detail = (await res.json()).detail; } catch { /* not JSON */ }
    throw new ApiError(res.status, detail ?? res.statusText);
  }
  return res;
}

export const files = {
  getBlob: async (path) => (await binary("GET", path)).blob(),
  put: async (path, blob) => (await binary("PUT", path, blob)).json(),
};

export const api = {
  get: (path) => request("GET", path),
  post: (path, body = {}) => request("POST", path, body),
  put: (path, body = {}) => request("PUT", path, body),
  patch: (path, body = {}) => request("PATCH", path, body),
  del: (path) => request("DELETE", path),
};

export function qs(params) {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== "") p.set(k, v);
  }
  const s = p.toString();
  return s ? `?${s}` : "";
}
