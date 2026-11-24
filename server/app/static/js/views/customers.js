// Customers: list for everyone, add/edit for everyone, delete for admins.
import { api } from "../api.js";
import { isAdmin, refreshCustomers, store } from "../store.js";
import { attempt, badge, clear, confirmDialog, debounce, h, modal, table } from "../ui.js";

const FIELDS = [
  ["name", "Name *"],
  ["number", "Kundennummer"],
  ["contact", "Ansprechpartner"],
  ["phone", "Telefon"],
  ["email", "E-Mail"],
  ["address", "Adresse", "textarea"],
  ["notes", "Notizen", "textarea"],
];

export async function render(root) {
  await refreshCustomers();
  const filter = h("input", { type: "search", placeholder: "Kunde suchen …", style: { maxWidth: "320px" } });
  const list = h("div", { class: "panel" });

  const draw = () => {
    const term = filter.value.trim().toLowerCase();
    const rows = store.customers.filter((c) =>
      !term || [c.name, c.number, c.contact].some((v) => v && v.toLowerCase().includes(term)));
    clear(list, table({
      columns: [
        { label: "Name", value: (c) => c.name },
        { label: "Kundennr.", value: (c) => c.number },
        { label: "Ansprechpartner", value: (c) => c.contact },
        { label: "Telefon", value: (c) => c.phone },
        { label: "E-Mail", value: (c) => (c.email ? h("a", { href: `mailto:${c.email}`, onclick: (e) => e.stopPropagation() }, c.email) : "") },
        { label: "Artikel", class: "num", value: (c) => String(c.parts ?? 0) },
        { label: "", value: (c) => (c.active ? "" : badge("inaktiv", "bad")) },
      ],
      rows,
      onRowClick: async (c) => { if (await customerDialog(c)) { await refreshCustomers(); draw(); } },
      empty: store.customers.length ? "Kein Kunde passt zur Suche" : "Noch keine Kunden angelegt",
    }));
  };
  filter.addEventListener("input", debounce(draw, 150));

  clear(
    root,
    h("div", { class: "page-head" },
      h("h1", {}, "Kunden"),
      h("div", { class: "row" }, filter,
        h("button", { class: "primary", onclick: async () => { if (await customerDialog(null)) draw(); } }, "+ Neuer Kunde"))),
    list,
  );
  draw();
}

// Add (customer = null) or edit. Resolves with the saved customer, or null if cancelled.
export function customerDialog(customer) {
  return new Promise((resolve) => {
    const isNew = !customer;
    let result = null;
    const inputs = Object.fromEntries(FIELDS.map(([key, , kind]) => {
      const value = customer?.[key] ?? "";
      return [key, kind === "textarea" ? h("textarea", { id: `c-${key}` }, value) : h("input", { id: `c-${key}`, value })];
    }));
    const body = h("div", { class: "form-grid" }, FIELDS.map(([key, label, kind]) =>
      h("label", { class: `field${kind === "textarea" ? " wide" : ""}`, for: `c-${key}` }, label, inputs[key])));
    if (!isNew && !customer.active) body.prepend(h("p", { class: "wide" }, badge("inaktiv", "bad"), " Wird bei neuen Artikeln nicht angeboten."));

    const values = () => Object.fromEntries(FIELDS.map(([key]) => [key, inputs[key].value.trim()]));
    const m = modal({
      title: isNew ? "Neuer Kunde" : customer.name,
      body,
      actions: [
        !isNew && isAdmin()
          ? {
            label: customer.active ? "Löschen" : "Wieder aktivieren",
            danger: customer.active,
            onClick: async () => {
              if (!customer.active) {
                result = await attempt(() => api.patch(`/customers/${customer.id}`, { active: true }), "Aktiviert");
                if (!result) return false;
                return;
              }
              const text = customer.parts
                ? `${customer.name} wird von ${customer.parts} Artikel(n) verwendet und wird deshalb nur deaktiviert.`
                : `${customer.name} endgültig löschen?`;
              if (!(await confirmDialog("Kunde löschen", text, customer.parts ? "Deaktivieren" : "Löschen", true))) return false;
              result = await attempt(() => api.del(`/customers/${customer.id}`), customer.parts ? "Deaktiviert" : "Gelöscht");
              if (!result) return false;
            },
          }
          : null,
        { label: "Abbrechen" },
        {
          label: "Speichern",
          primary: true,
          onClick: async () => {
            if (!inputs.name.value.trim()) { inputs.name.focus(); return false; }
            result = await attempt(
              () => (isNew ? api.post("/customers", values()) : api.patch(`/customers/${customer.id}`, values())),
              isNew ? "Kunde angelegt" : "Gespeichert",
            );
            if (!result) return false;
            await refreshCustomers();
          },
        },
      ].filter(Boolean),
    });
    // resolve when the dialog goes away, however it was closed
    const watch = new MutationObserver(() => {
      if (!m.box.isConnected) { watch.disconnect(); resolve(result); }
    });
    watch.observe(document.body, { childList: true });
  });
}
