// Shared data most screens need (current user, fields, machines, users), loaded once and
// refreshed after admin changes.
import { api } from "./api.js";

export const store = {
  me: null,
  fields: { article: [], operation: [] },
  machines: [],
  users: [],
  customers: [], // all, including inactive (old parts still show the name)
};

export async function loadSession() {
  store.me = await api.get("/auth/me");
  await refreshCatalog();
}

export async function refreshCatalog() {
  const [fields, machines, users] = await Promise.all([api.get("/fields"), api.get("/machines"), api.get("/users")]);
  store.fields.article = fields.filter((f) => f.entity === "article");
  store.fields.operation = fields.filter((f) => f.entity === "operation");
  store.machines = machines;
  store.users = users;
  await refreshCustomers();
}

export async function refreshCustomers() {
  store.customers = await api.get("/customers?include_inactive=true");
}

export function customerName(id) {
  const c = store.customers.find((x) => x.id === id);
  return c ? c.name : id == null ? "" : `#${id}`;
}

export function isAdmin() {
  return store.me && store.me.role === "admin";
}

export function userName(id) {
  const u = store.users.find((x) => x.id === id);
  return u ? u.display_name : id == null ? "" : `#${id}`;
}

export function machineName(id) {
  const m = store.machines.find((x) => x.id === id);
  return m ? m.name : `#${id}`;
}

export function field(entity, key) {
  return store.fields[entity].find((f) => f.key === key);
}
