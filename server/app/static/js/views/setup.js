// First-run setup of a new installation: shown after the first login of the built-in admin
// (admin / admin). Steps: welcome -> administrator (own password) -> folders -> fields ->
// machines -> numbers -> summary. Also renderPasswordChange: new password when an admin still
// has the default one.
import { api } from "../api.js";
import { clear, h, toast } from "../ui.js";

// same as SIMPLE_FIELDS on the server
const SIMPLE = {
  article: ["teilename", "zeichnungsnummer", "artikelnummer", "kunde", "status", "notiz"],
  operation: ["prozessbeschreibung", "cam_programmierer", "operations_status", "material", "kommentar"],
};

const STEPS = ["Willkommen", "Administrator", "Ordner", "Felder", "Maschinen", "Nummern", "Fertig"];

export function render(root, onDone, status = {}) {
  const data = {
    company: "",
    admin: { username: "admin", display_name: "", password: "", password2: "" },
    folders: { mcam_root: status.suggest?.mcam_root || "", nc_root: status.suggest?.nc_root || "" },
    fields: "full",
    machines: [],
    mdm_start: 0,
  };
  let step = 0;
  // the fields of this installation (the admin is logged in), ticked by preset or one by one
  let fieldDefs = [];
  const enabled = { article: new Set(), operation: new Set() };
  const applyPreset = (preset) => {
    if (preset === "custom") return;
    for (const f of fieldDefs) {
      if (f.system) continue;
      const on = preset === "full" || (SIMPLE[f.entity] || []).includes(f.key);
      enabled[f.entity][on ? "add" : "delete"](f.key);
    }
  };
  api.get("/admin/fields").then((list) => { fieldDefs = list; applyPreset(data.fields); if (step === 3) show(); }).catch(() => { /* presets still work */ });
  const body = h("div", { class: "panel-body setup-body" });
  const nav = h("div", { class: "setup-steps" });

  const field = (label, input, hint) => h("label", { class: "field wide" }, label, input, hint ? h("span", { class: "muted setup-hint" }, hint) : null);
  const bind = (obj, key, attrs = {}) => {
    const el = h("input", { value: obj[key] ?? "", ...attrs });
    el.addEventListener("input", () => { obj[key] = attrs.type === "number" ? Number(el.value || 0) : el.value; });
    return el;
  };

  const pages = [
    // 0 welcome
    () => [
      h("h2", {}, "PDB einrichten"),
      h("p", {}, "PDB verwaltet Mastercam-Dateien: Teile (Karten) mit ihren Operationen, Sperren beim Bearbeiten, Versionen, NC-Export mit Programmkopf und Maschinenwechsel."),
      h("p", { class: "muted" }, "Diese Einrichtung erscheint nur einmal, bei der ersten Anmeldung. Alles lässt sich später in den Admin-Einstellungen ändern."),
      field("Firmenname (erscheint oben in der Leiste, optional)", bind(data, "company", { placeholder: "z. B. Muster Feinmechanik AG" })),
    ],
    // 1 admin
    () => [
      h("h2", {}, "Administrator"),
      h("p", { class: "muted" }, "Dein Administrator-Konto verwaltet Benutzer, Maschinen, Felder und Einstellungen. Das Standard-Passwort admin wird hier durch dein eigenes ersetzt. Admins brauchen immer ein Passwort; normale Benutzer können später auch ohne Passwort arbeiten."),
      h("div", { class: "form-grid" },
        h("label", { class: "field" }, "Anzeigename", bind(data.admin, "display_name", { placeholder: "Vorname Nachname" })),
        h("label", { class: "field" }, "Benutzername", bind(data.admin, "username")),
        h("label", { class: "field" }, "Neues Passwort (mind. 6 Zeichen)", bind(data.admin, "password", { type: "password", autocomplete: "new-password" })),
        h("label", { class: "field" }, "Passwort wiederholen", bind(data.admin, "password2", { type: "password", autocomplete: "new-password" }))),
    ],
    // 2 folders
    () => [
      h("h2", {}, "Ordner"),
      h("p", { class: "muted" }, "Pfade so, wie die Arbeitsplätze sie sehen (Netzlaufwerk oder \\\\server\\freigabe). Jede Maschine bekommt darunter einen eigenen Ordner, jede Operation darin einen Ordner MDM<Nummer>."),
      field("Hauptordner für Mastercam-Dateien", bind(data.folders, "mcam_root", { placeholder: "M:\\CAM\\Maschinen" }),
        "Beispiel: M:\\CAM\\Maschinen\\DMU50\\MDM1001\\00001001.mcam"),
      field("Hauptordner für NC-Programme (DNC)", bind(data.folders, "nc_root", { placeholder: "H:\\NC" }),
        "Beispiel: H:\\NC\\DMU50\\00001001.H – oder 1101.NC bei FANUC-Maschinen"),
    ],
    // 3 fields
    () => {
      const choice = (value, title, text) => h("label", { class: `setup-choice${data.fields === value ? " active" : ""}` },
        h("input", { type: "radio", name: "fields", checked: data.fields === value, onchange: () => { data.fields = value; applyPreset(value); show(); } }),
        h("div", {}, h("strong", {}, title), h("div", { class: "muted" }, text)));
      // one checkbox per field; the presets just tick a set of them
      const list = (entity, title) => h("fieldset", { class: "setup-fields" },
        h("legend", {}, title),
        fieldDefs.filter((f) => f.entity === entity).map((f) => h("label", { class: "check", title: f.system ? "Systemfeld: wird für Sperren, Export oder Pfade gebraucht und kann nicht ausgeschaltet werden." : "" },
          h("input", {
            type: "checkbox",
            checked: f.system || enabled[entity].has(f.key),
            disabled: f.system,
            onchange: (e) => {
              enabled[entity][e.target.checked ? "add" : "delete"](f.key);
              data.fields = "custom";
              show();
            },
          }), f.label)));
      return [
        h("h2", {}, "Felder auf den Karten"),
        choice("full", "Vollständig (wie MDM)", "Alle Felder: Teilename, Zeichnungs-, Artikel-, Bestell-, Auftrags- und Projektnummer, Index, Stückzahl, Kunde, Rohteil, Termine, Status; bei Operationen Programmierer, Bediener, Spannmittel, Lagerort, Zeiten …"),
        choice("simple", "Einfach", "Nur das Wichtigste: Teilename, Zeichnungs- und Artikelnummer, Kunde, Status, Notiz; Operation, Programmierer, Status, Material, Kommentar. Weitere Felder lassen sich jederzeit einschalten."),
        data.fields === "custom" ? choice("custom", "Eigene Auswahl", "Unten einzeln gewählt.") : null,
        fieldDefs.length ? h("p", { class: "muted section" }, "Einzeln ein- und ausschalten (später unter Admin → Felder, dort auch eigene Felder anlegen):") : null,
        fieldDefs.length ? h("div", { class: "setup-field-lists" }, list("article", "Artikel (Karte)"), list("operation", "Operation (Prozessdaten)")) : null,
      ];
    },
    // 4 machines
    () => {
      const list = h("div", { class: "setup-machines" });
      const draw = () => clear(list,
        data.machines.length ? data.machines.map((m, i) => h("div", { class: "setup-machine" },
          h("input", { value: m.name, placeholder: "Name, z. B. DMU 50", "aria-label": "Name", oninput: (e) => { m.name = e.target.value; } }),
          h("input", { value: m.mcam_folder, placeholder: "Mastercam-Ordner", "aria-label": "Mastercam-Ordner", oninput: (e) => { m.mcam_folder = e.target.value; } }),
          h("input", { value: m.nc_folder, placeholder: "NC-Ordner", "aria-label": "NC-Ordner", oninput: (e) => { m.nc_folder = e.target.value; } }),
          h("select", { "aria-label": "NC-Dateiname", onchange: (e) => { m.nc_naming = e.target.value; } },
            h("option", { value: "mdm", selected: m.nc_naming === "mdm" }, "MDM-Nr. (Heidenhain)"),
            h("option", { value: "nc", selected: m.nc_naming === "nc" }, "NC-Nr. (FANUC)")),
          h("input", { value: m.nc_extension, placeholder: ".H", "aria-label": "Endung", oninput: (e) => { m.nc_extension = e.target.value; } }),
          h("input", { class: "wide-input", value: m.machine_definition, placeholder: "Maschinendefinition (.mcam-mmd), optional", "aria-label": "Maschinendefinition", oninput: (e) => { m.machine_definition = e.target.value.replace(/^"(.*)"$/, "$1"); } }),
          h("button", { type: "button", class: "small danger", onclick: () => { data.machines.splice(i, 1); draw(); } }, "×")))
          : h("p", { class: "muted" }, "Noch keine Maschine. Du kannst sie auch später unter Maschinen anlegen."));
      draw();
      return [
        h("h2", {}, "Maschinen"),
        h("p", { class: "muted" }, "Pro Maschine: Ordnernamen unter den beiden Hauptordnern, wie NC-Dateien heissen (MDM-Nummer oder NC-Programmnummer für FANUC/O-Nummern), Dateiendung und optional die Mastercam-Maschinendefinition."),
        list,
        h("button", {
          type: "button",
          onclick: () => {
            data.machines.push({ name: "", mcam_folder: "", nc_folder: "", nc_naming: "mdm", nc_extension: ".H", machine_definition: "" });
            draw();
          },
        }, "+ Maschine"),
      ];
    },
    // 5 numbers
    () => [
      h("h2", {}, "Nummern"),
      h("p", {}, "Jede Operation bekommt eine fortlaufende MDM-Nummer; sie benennt Ordner und NC-Dateien."),
      h("p", { class: "muted" }, "Neu anfangen: 0 lassen (erste Nummer 1). Wer von einem anderen System umsteigt, setzt hier die höchste bisher verwendete Nummer, damit neue Programme alte auf den Maschinen nie überschreiben."),
      field("Zuletzt verwendete Nummer", bind(data, "mdm_start", { type: "number", min: "0" })),
    ],
    // 6 summary
    () => [
      h("h2", {}, "Zusammenfassung"),
      h("div", { class: "kv" }, h("div", { class: "k" }, "Firma"), h("div", { class: "v" }, data.company || "–")),
      h("div", { class: "kv" }, h("div", { class: "k" }, "Administrator"), h("div", { class: "v" }, `${data.admin.display_name} (${data.admin.username})`)),
      h("div", { class: "kv" }, h("div", { class: "k" }, "Mastercam-Ordner"), h("div", { class: "v" }, data.folders.mcam_root)),
      h("div", { class: "kv" }, h("div", { class: "k" }, "NC-Ordner"), h("div", { class: "v" }, data.folders.nc_root)),
      h("div", { class: "kv" }, h("div", { class: "k" }, "Felder"), h("div", { class: "v" }, data.fields === "full" ? "Vollständig" : data.fields === "simple" ? "Einfach" : "Eigene Auswahl")),
      h("div", { class: "kv" }, h("div", { class: "k" }, "Maschinen"), h("div", { class: "v" }, data.machines.map((m) => m.name).join(", ") || "keine")),
      h("div", { class: "kv" }, h("div", { class: "k" }, "Erste MDM-Nummer"), h("div", { class: "v" }, String(Number(data.mdm_start || 0) + 1))),
      h("p", { class: "muted section" }, "Danach: Benutzer anlegen, auf jedem Arbeitsplatz den PDB Helper einrichten (von der IT), in Mastercam die PDB-Bridge laden."),
    ],
  ];

  // what must be filled before "Weiter"
  const check = () => {
    if (step === 1) {
      if (!data.admin.display_name.trim() || !data.admin.username.trim()) return "Name und Benutzername angeben.";
      if (data.admin.password.length < 6) return "Passwort mit mindestens 6 Zeichen.";
      if (data.admin.password !== data.admin.password2) return "Die Passwörter sind nicht gleich.";
      if (data.admin.password === "admin") return "Bitte ein anderes Passwort als das Standard-Passwort wählen.";
    }
    if (step === 2 && (data.folders.mcam_root.trim().length < 3 || data.folders.nc_root.trim().length < 3)) return "Beide Ordner angeben.";
    if (step === 4) {
      data.machines = data.machines.filter((m) => m.name.trim() || m.mcam_folder.trim());
      const bad = data.machines.find((m) => !m.name.trim() || !m.mcam_folder.trim() || !m.nc_folder.trim());
      if (bad) return "Jede Maschine braucht Name, Mastercam-Ordner und NC-Ordner.";
      const badFolder = data.machines.find((m) => /[<>:"/\\|?*]/.test(m.mcam_folder + m.nc_folder));
      if (badFolder) return `${badFolder.name}: nur den Ordnernamen angeben, ohne \\ / :`;
    }
    return null;
  };

  const finish = async (btn) => {
    btn.disabled = true;
    try {
      const { password2, ...admin } = data.admin; // eslint-disable-line no-unused-vars
      const enabled_fields = fieldDefs.length ? { article: [...enabled.article], operation: [...enabled.operation] } : null;
      const res = await api.post("/setup", { ...data, admin, enabled_fields, mdm_start: Number(data.mdm_start || 0) });
      toast("PDB ist eingerichtet", "ok");
      await onDone(res.token);
    } catch (err) {
      toast(err.message, "error");
      btn.disabled = false;
    }
  };

  const show = () => {
    clear(nav, STEPS.map((s, i) => h("span", { class: `setup-step${i === step ? " active" : i < step ? " done" : ""}` }, `${i + 1}. ${s}`)));
    const back = h("button", { type: "button", disabled: step === 0, onclick: () => { step--; show(); } }, "‹ Zurück");
    const next = step === pages.length - 1
      ? h("button", { type: "button", class: "primary", onclick: (e) => finish(e.currentTarget) }, "Einrichten")
      : h("button", {
        type: "button",
        class: "primary",
        onclick: () => {
          const problem = check();
          if (problem) { toast(problem, "error"); return; }
          step++;
          show();
        },
      }, "Weiter ›");
    clear(body, pages[step](), h("div", { class: "row section" }, back, h("span", { class: "spacer" }), next));
    body.querySelector("input")?.focus();
  };

  clear(root, h("div", { class: "login panel setup" }, h("div", { class: "panel-title" }, "PDB – Ersteinrichtung"), nav, body));
  show();
}

// An admin still has the default password: set a new one before anything else.
export function renderPasswordChange(root, onDone) {
  const pass = h("input", { type: "password", autocomplete: "new-password" });
  const pass2 = h("input", { type: "password", autocomplete: "new-password" });
  const form = h("form", {
    onsubmit: async (e) => {
      e.preventDefault();
      if (pass.value.length < 6) return toast("Passwort mit mindestens 6 Zeichen.", "error");
      if (pass.value !== pass2.value) return toast("Die Passwörter sind nicht gleich.", "error");
      if (pass.value === "admin") return toast("Bitte ein anderes Passwort als das Standard-Passwort wählen.", "error");
      try {
        await api.post("/auth/me/password", { current_password: "admin", new_password: pass.value });
        toast("Passwort gespeichert", "ok");
        await onDone(null);
      } catch (err) { toast(err.message, "error"); }
    },
  },
  h("h2", {}, "Neues Passwort festlegen"),
  h("p", { class: "muted" }, "Dein Konto hat noch das Standard-Passwort. Bitte zuerst ein eigenes festlegen."),
  h("label", { class: "field" }, "Neues Passwort (mind. 6 Zeichen)", pass),
  h("label", { class: "field" }, "Passwort wiederholen", pass2),
  h("div", { class: "row section" }, h("span", { class: "spacer" }), h("button", { class: "primary", type: "submit" }, "Passwort speichern")));
  clear(root, h("div", { class: "login panel" }, h("div", { class: "panel-title" }, "PDB Dateiverwaltung"), h("div", { class: "panel-body" }, form)));
  pass.focus();
}
