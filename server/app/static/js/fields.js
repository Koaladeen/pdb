// Renders the configurable fields. Everything here is driven by the field definitions from
// the server, so new or disabled fields show up without code changes.
import { api } from "./api.js";
import { customerName, store, userName } from "./store.js";
import { debounce, formatDate, h } from "./ui.js";
import { customerDialog } from "./views/customers.js";

// Display text for a stored value.
export function displayValue(fd, value) {
  if (value === undefined || value === null || value === "") return "";
  switch (fd.type) {
    case "user": return userName(value);
    case "customer": return customerName(value);
    case "date": return formatDate(value);
    case "boolean": return value ? "Ja" : "Nein";
    default: return String(value);
  }
}

// One input for one field. Returns {el, input, get()}; get() returns the typed value or null.
export function fieldInput(fd, value) {
  const id = `f-${fd.entity}-${fd.key}`;
  let input;
  let get;
  switch (fd.type) {
    case "longtext":
      input = h("textarea", { id }, value ?? "");
      get = () => input.value.trim() === "" ? null : input.value;
      break;
    case "choice":
      input = h(
        "select",
        { id },
        h("option", { value: "" }, "–"),
        fd.choices.map((c) => h("option", { value: c.value, selected: c.value === value }, c.value)),
      );
      if (value && !fd.choices.some((c) => c.value === value)) {
        input.append(h("option", { value, selected: true }, `${value} (nicht mehr gültig)`));
      }
      get = () => input.value || null;
      break;
    case "user":
      input = h(
        "select",
        { id },
        h("option", { value: "" }, "–"),
        store.users
          .filter((u) => u.active || u.id === value)
          .map((u) => h("option", { value: u.id, selected: u.id === value }, u.display_name)),
      );
      get = () => (input.value ? Number(input.value) : null);
      break;
    case "customer": {
      // Dropdown of customers; "+ Neu" adds one without leaving the form.
      const options = () => [
        h("option", { value: "" }, "–"),
        store.customers
          .filter((c) => c.active || c.id === value)
          .map((c) => h("option", { value: c.id, selected: c.id === value }, c.active ? c.name : `${c.name} (inaktiv)`)),
      ];
      input = h("select", { id }, options());
      const add = h("button", {
        type: "button",
        title: "Neuen Kunden anlegen",
        onclick: async () => {
          const created = await customerDialog(null);
          if (!created) return;
          input.replaceChildren(...options().flat());
          input.value = String(created.id);
        },
      }, "+ Neu");
      get = () => (input.value ? Number(input.value) : null);
      const row = h("div", { class: "row", style: { flexWrap: "nowrap" } }, input, add);
      const el = h("label", { class: "field", for: id }, h("span", {}, fd.label, fd.required ? h("span", { class: "req" }, " *") : null), row);
      return { el, input, get, fd };
    }
    case "integer":
    case "number":
      input = h("input", { id, type: "number", step: fd.type === "integer" ? "1" : "any", value: value ?? "" });
      get = () => (input.value === "" ? null : Number(input.value));
      break;
    case "date":
      input = h("input", { id, type: "date", value: value ?? "" });
      get = () => input.value || null;
      break;
    case "duration":
      input = h("input", { id, placeholder: "00:00:00", pattern: "\\d{1,4}:[0-5]\\d:[0-5]\\d", value: value ?? "" });
      get = () => (input.value.trim() === "" ? null : input.value.trim());
      break;
    case "boolean":
      input = h("input", { id, type: "checkbox", checked: Boolean(value) });
      get = () => input.checked;
      break;
    case "suggest": {
      const listId = `${id}-list`;
      const list = h("datalist", { id: listId });
      input = h("input", { id, list: listId, value: value ?? "", autocomplete: "off" });
      const load = debounce(async () => {
        try {
          const values = await api.get(`/suggest/${fd.entity}/${fd.key}?q=${encodeURIComponent(input.value)}`);
          list.replaceChildren(...values.map((v) => h("option", { value: v })));
        } catch { /* suggestions are optional */ }
      });
      input.addEventListener("focus", load);
      input.addEventListener("input", load);
      get = () => (input.value.trim() === "" ? null : input.value.trim());
      return wrap(fd, input, get, list);
    }
    default:
      input = h("input", { id, value: value ?? "" });
      get = () => (input.value.trim() === "" ? null : input.value.trim());
  }
  return wrap(fd, input, get);
}

function wrap(fd, input, get, extra = null) {
  const wide = fd.type === "longtext";
  const el = fd.type === "boolean"
    ? h("label", { class: `check${wide ? " wide" : ""}` }, input, fd.label)
    : h("label", { class: `field${wide ? " wide" : ""}`, for: input.id }, h("span", {}, fd.label, fd.required ? h("span", { class: "req" }, " *") : null), input, extra);
  return { el, input, get, fd };
}

// Form for all enabled, non-system fields of an entity.
// values() returns only the fields that changed (null = cleared), ready for PATCH/POST.
export function fieldForm(entity, data = {}) {
  const inputs = store.fields[entity].filter((fd) => !fd.system).map((fd) => fieldInput(fd, data[fd.key]));
  const el = h("div", { class: "form-grid" }, inputs.map((i) => i.el));
  return {
    el,
    values() {
      const out = {};
      for (const i of inputs) {
        const v = i.get();
        const before = data[i.fd.key] ?? null;
        if (v !== before) out[i.fd.key] = v;
      }
      return out;
    },
  };
}

// Read-only key/value list (part card view).
export function fieldList(entity, data) {
  return store.fields[entity]
    .filter((fd) => !fd.system)
    .map((fd) => h("div", { class: "kv" }, h("div", { class: "k" }, fd.label), h("div", { class: "v" }, displayValue(fd, data[fd.key]))));
}

// Value of an operation column, including the system fields stored as columns.
export function operationCell(fd, op) {
  switch (fd.key) {
    case "op_number": return String(op.op_number);
    case "mdm_number": return op.mdm_number_text;
    case "nc_number": return op.nc_number == null ? "" : String(op.nc_number);
    case "machine_id": return op.machine;
    default: return displayValue(fd, op.data[fd.key]);
  }
}
