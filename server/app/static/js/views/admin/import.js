// Admin: take over existing Mastercam files. 1) pick files (folder in the browser - only names,
// sizes and dates are read, nothing is uploaded - or paste a list of paths), 2) group them into
// cards (suggestions by folder / file name, then freely: new cards, existing cards, order,
// machine, title per file), 3) import: every file becomes an operation with a new MDM number and
// is copied into its operation folder (by the server if it can reach it, otherwise by the PDB
// Helper on first open). The original files are never changed.
import { api, qs } from "../../api.js";
import { store } from "../../store.js";
import { attempt, clear, debounce, h, modal, toast } from "../../ui.js";

const MCAM = /\.(mcam|mcx(-\d+)?)$/i;
const SKIP_FOLDERS = /(^|[\\/])(revisionen|revisions?|backup|backups|archiv|archive)([\\/]|$)/i;
const MAX_CARDS_SHOWN = 300;

let seq = 0;
const nid = () => ++seq;
const norm = (s) => (s || "").toLowerCase().replace(/[^a-z0-9]/g, "");
const stem = (name) => name.replace(/\.[^.]+$/, "");
const fmtSize = (n) => (n == null ? "" : n > 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);

export async function render(root) {
  const [machines, pending] = await Promise.all([api.get("/admin/machines"), api.get("/admin/import/pending")]);
  const active = machines.filter((m) => m.active);
  const st = {
    files: [], // {id, rel, name, folder, size, modified, pickId | fullPath, machine_id, title, known}
    cards: [], // {id, article_id, label, data: {teilename, zeichnungsnummer, kunde}, ops: [fileId]}
    picks: [], // each folder/file pick: {id, name, base} - base = where it is on the PCs
    skipArchive: true,
    filter: "",
    selected: new Set(),
    copyNow: true,
    result: null,
  };
  const byId = (id) => st.files.find((f) => f.id === id);
  const pickOf = (f) => st.picks.find((p) => p.id === f.pickId);
  const hasBase = (f) => !f.pickId || pickOf(f).base.trim().length >= 3;
  // picked in the browser: the pick's base path + the path inside it; pasted: as given
  const fullPath = (f) => (f.pickId ? `${pickOf(f).base.trim().replace(/[\\/]+$/, "")}\\${f.rel.replace(/\//g, "\\")}` : f.fullPath);
  const assigned = () => new Set(st.cards.flatMap((c) => c.ops));
  const unassigned = () => { const a = assigned(); return st.files.filter((f) => !a.has(f.id) && !f.known); };

  const guessMachine = (f) => {
    const segs = `${fullPath(f) || ""}\\${f.folder}`.split(/[\\/]/).map(norm).filter(Boolean);
    const m = active.find((x) => segs.includes(norm(x.mcam_folder)) || segs.includes(norm(x.name)));
    return m ? m.id : null;
  };

  // ------------------------------------------------------------ 1) files
  const addFiles = (list) => {
    let added = 0, skipped = 0;
    for (const f of list) {
      if (!MCAM.test(f.name)) continue;
      if (st.skipArchive && SKIP_FOLDERS.test(f.folder)) { skipped++; continue; }
      const keyOf = (x) => (x.pickId ? `${x.pickId}|${x.rel}` : x.fullPath).toLowerCase();
      if (st.files.some((x) => keyOf(x) === keyOf(f))) continue;
      st.files.push({ id: nid(), machine_id: null, title: stem(f.name), known: null, ...f });
      added++;
    }
    for (const f of st.files) if (f.machine_id == null) f.machine_id = guessMachine(f);
    toast(`${added} Dateien übernommen${skipped ? `, ${skipped} aus Archiv-Ordnern ausgelassen` : ""}`, added ? "ok" : "");
    checkKnown();
    draw();
  };

  const fromPicker = (input) => {
    const files = [...input.files];
    input.value = "";
    if (!files.length) return;
    // a folder pick: the first path part is the picked folder itself, paths are relative to it
    const folderName = files[0].webkitRelativePath ? files[0].webkitRelativePath.split("/")[0] : "";
    const pick = { id: nid(), name: folderName, base: "" };
    st.picks.push(pick);
    const list = files.map((file) => {
      const inner = file.webkitRelativePath ? file.webkitRelativePath.split("/").slice(1).join("/") : file.name;
      return { rel: inner, name: file.name, folder: inner.split("/").slice(0, -1).join("/"), size: file.size,
        modified: new Date(file.lastModified).toISOString().slice(0, 19), pickId: pick.id };
    });
    addFiles(list);
    st.picks = st.picks.filter((p) => st.files.some((f) => f.pickId === p.id)); // nothing usable in it
    draw();
  };

  const fromText = (text) => addFiles(text.split(/\r?\n/).map((l) => l.trim().replace(/^"|"$/g, "")).filter(Boolean).map((p) => {
    const parts = p.split(/[\\/]/);
    return { rel: p, fullPath: p, name: parts[parts.length - 1], folder: parts.slice(0, -1).join("\\"), size: null, modified: "" };
  }));

  const checkKnown = debounce(async () => {
    const withPath = st.files.filter(hasBase);
    if (!withPath.length) return;
    try {
      const { known } = await api.post("/admin/import/check", { paths: withPath.map(fullPath) });
      for (const f of withPath) f.known = known[fullPath(f)] || null;
      // already imported files leave their cards (the server would skip them anyway)
      for (const c of st.cards) c.ops = c.ops.filter((id) => !byId(id).known);
      st.cards = st.cards.filter((c) => c.ops.length);
      draw();
    } catch { /* checked again at import */ }
  }, 500);

  // ------------------------------------------------------------ 2) cards
  const newCard = (fileIds, teilename = "") => {
    const card = { id: nid(), article_id: null, label: "", data: { teilename, zeichnungsnummer: "", kunde: null }, ops: [] };
    card.ops = [...fileIds].sort((a, b) => byId(a).name.localeCompare(byId(b).name, undefined, { numeric: true }));
    st.cards.push(card);
    return card;
  };
  const suggest = (how) => {
    const groups = new Map();
    for (const f of unassigned()) {
      let key, name;
      if (how === "folder") { key = f.folder.toLowerCase(); key = `${f.pickId || ""}|${key}`; name = f.folder.split(/[\\/]/).pop() || pickOf(f)?.name || stem(f.name); }
      else if (how === "prefix") { name = stem(f.name).split(/[_\-\s.]+/)[0] || stem(f.name); key = name.toLowerCase(); }
      else { key = `#${f.id}`; name = stem(f.name); }
      if (!groups.has(key)) groups.set(key, { name, ids: [] });
      groups.get(key).ids.push(f.id);
    }
    for (const g of groups.values()) newCard(g.ids, g.name);
    st.selected.clear();
    draw();
  };
  const removeCard = (card) => { st.cards = st.cards.filter((c) => c !== card); draw(); };
  const unassign = (card, id) => { card.ops = card.ops.filter((x) => x !== id); if (!card.ops.length) removeCard(card); else draw(); };
  const move = (card, i, d) => { const j = i + d; if (j < 0 || j >= card.ops.length) return; [card.ops[i], card.ops[j]] = [card.ops[j], card.ops[i]]; draw(); };

  const pickExisting = (card) => {
    const input = h("input", { placeholder: "Name, Nummer, Kunde …", autofocus: true });
    const list = h("div", { class: "import-pick" });
    const search = debounce(async () => {
      const data = await attempt(() => api.get(`/articles${qs({ q: input.value, limit: 15 })}`));
      if (!data) return;
      clear(list, data.items.length ? data.items.map((a) => h("button", {
        type: "button",
        class: "import-pick-row",
        onclick: () => {
          card.article_id = a.id;
          card.label = [a.data.teilename, a.data.zeichnungsnummer].filter(Boolean).join(" · ") || `#${a.id}`;
          dlg.close();
          draw();
        },
      }, h("strong", {}, a.data.teilename || `#${a.id}`), h("span", { class: "muted" }, [a.data.zeichnungsnummer, `${a.operations.length} OP`].filter(Boolean).join(" · ")))) : h("p", { class: "muted" }, "Keine Artikel gefunden"));
    }, 250);
    input.addEventListener("input", search);
    const dlg = modal({ title: "Bestehende Karte wählen", body: h("div", {}, input, list), actions: [{ label: "Abbrechen" }] });
    search();
    input.focus();
  };

  const machineSelect = (value, onchange) => h("select", { onchange: (e) => onchange(e.target.value ? Number(e.target.value) : null), "aria-label": "Maschine" },
    h("option", { value: "", selected: value == null }, "– Maschine –"),
    active.map((m) => h("option", { value: m.id, selected: m.id === value }, m.name)));

  const customerSelect = (card) => h("select", { "aria-label": "Kunde", onchange: (e) => { card.data.kunde = e.target.value ? Number(e.target.value) : null; } },
    h("option", { value: "" }, "– Kunde –"),
    store.customers.filter((c) => c.active !== false).map((c) => h("option", { value: c.id, selected: c.id === card.data.kunde }, c.name)));

  // ------------------------------------------------------------ 3) import
  const problems = () => {
    if (st.cards.flatMap((c) => c.ops).map(byId).some((f) => !hasBase(f))) return "Bitte angeben, wo der gewählte Ordner auf den PCs liegt.";
    if (!st.cards.length) return "Noch keine Karten gebildet.";
    const noName = st.cards.find((c) => !c.article_id && !c.data.teilename.trim());
    if (noName) return "Jede neue Karte braucht einen Teilenamen.";
    const noMachine = st.cards.flatMap((c) => c.ops).map(byId).find((f) => !f.machine_id);
    if (noMachine) return `${noMachine.name}: keine Maschine gewählt.`;
    return null;
  };
  const runImport = async (btn) => {
    const p = problems();
    if (p) return toast(p, "error");
    btn.disabled = true;
    const body = {
      copy_now: st.copyNow,
      cards: st.cards.map((c) => ({
        article_id: c.article_id,
        article: c.article_id ? {} : Object.fromEntries(Object.entries(c.data).filter(([, v]) => v !== "" && v != null)),
        operations: c.ops.map(byId).map((f) => ({ source_path: fullPath(f), machine_id: f.machine_id, title: f.title, size: f.size, modified: f.modified })),
      })),
    };
    try {
      st.result = await api.post("/admin/import/files", body);
      pending.count += st.result.pending;
      st.picks = [];
      st.result.labels = st.cards.map((c) => c.label || c.data.teilename);
      st.files = []; st.cards = []; st.selected.clear();
      toast(`${st.result.imported} Dateien importiert`, "ok");
      draw();
    } catch (err) {
      toast(err.message, "error");
      btn.disabled = false;
    }
  };

  // ------------------------------------------------------------ drawing
  const folderInput = h("input", { type: "file", webkitdirectory: "", multiple: true, style: { display: "none" }, onchange: (e) => fromPicker(e.target) });
  const fileInput = h("input", { type: "file", multiple: true, accept: ".mcam,.mcx,.mcx-5,.mcx-6,.mcx-7,.mcx-8,.mcx-9", style: { display: "none" }, onchange: (e) => fromPicker(e.target) });

  const pendingPanel = (info) => (info.count ? h("div", { class: "panel import-pending" },
    h("div", { class: "panel-body row" },
      h("span", {}, `Noch nicht kopiert: ${info.count} importierte Dateien. Der PDB Helper kopiert sie beim ersten Öffnen.`),
      h("span", { class: "spacer" }),
      h("button", {
        type: "button",
        onclick: async () => {
          const r = await attempt(() => api.post("/admin/import/copy", {}));
          if (r) { toast(`${r.copied} kopiert, ${r.pending} noch offen`, r.copied ? "ok" : ""); Object.assign(info, { count: r.pending }); draw(); }
        },
      }, "Jetzt vom Server kopieren"))) : null);

  const step1 = () => h("div", { class: "panel" },
    h("div", { class: "panel-title" }, "1. Dateien wählen"),
    h("div", { class: "panel-body" },
      h("p", { class: "muted" }, "Nur Namen, Grösse und Datum werden gelesen – es wird nichts hochgeladen. Die Originale bleiben unverändert; PDB kopiert jede Datei in ihren eigenen MDM-Ordner."),
      h("div", { class: "row" },
        h("button", { type: "button", class: "primary", onclick: () => folderInput.click() }, "Ordner wählen …"),
        h("button", { type: "button", onclick: () => fileInput.click() }, "Einzelne Dateien …"),
        h("button", { type: "button", onclick: () => pasteDialog() }, "Pfadliste einfügen …"),
        h("label", { class: "check" }, h("input", { type: "checkbox", checked: st.skipArchive, onchange: (e) => { st.skipArchive = e.target.checked; } }), "Ordner wie Revisionen, Backup, Archiv auslassen"),
        folderInput, fileInput),
      // one "where is it on the PCs" per pick: the browser only knows the folder name
      st.picks.map((p) => h("label", { class: "field wide section" },
        p.name ? `Wo liegt der gewählte Ordner „${p.name}“ auf den PCs?` : "Wo liegt der gewählte Ordner auf den PCs?",
        h("input", {
          "data-k": `base-${p.id}`,
          value: p.base,
          placeholder: `M:\\Alte Programme${p.name ? `\\${p.name}` : ""}`,
          oninput: (e) => {
            p.base = e.target.value;
            for (const f of st.files) if (f.pickId === p.id && f.machine_id == null) f.machine_id = guessMachine(f);
            checkKnown();
          },
        }),
        h("span", { class: "muted setup-hint" }, "Der Browser kennt nur den Ordnernamen. Den vollen Pfad (Laufwerk oder \\\\server\\freigabe) braucht der PDB Helper zum Kopieren."))),
      st.files.length ? h("p", { class: "muted section" }, `${st.files.length} Dateien · ${st.files.filter((f) => f.known).length} schon importiert`) : null));

  const pasteDialog = () => {
    const ta = h("textarea", { rows: 10, placeholder: "M:\\Programme\\4711\\4711_OP1.mcam\nM:\\Programme\\4711\\4711_OP2.mcam" });
    modal({
      title: "Pfadliste einfügen",
      body: h("div", {}, h("p", { class: "muted" }, "Ein Pfad pro Zeile, z. B. aus: dir /s /b M:\\Programme\\*.mcam"), ta),
      actions: [{ label: "Abbrechen" }, { label: "Übernehmen", primary: true, onClick: () => fromText(ta.value) }],
    });
  };

  const fileRow = (f) => h("div", { class: `import-file${st.selected.has(f.id) ? " sel" : ""}` },
    h("input", { type: "checkbox", checked: st.selected.has(f.id), "aria-label": f.name, onchange: (e) => { e.target.checked ? st.selected.add(f.id) : st.selected.delete(f.id); draw(); } }),
    h("div", { class: "import-name", title: fullPath(f) || f.rel }, h("strong", {}, f.name), h("span", { class: "muted" }, [f.folder, fmtSize(f.size)].filter(Boolean).join(" · "))),
    machineSelect(f.machine_id, (v) => { f.machine_id = v; }));

  const step2 = () => {
    const free = unassigned();
    const visible = st.filter ? free.filter((f) => `${f.folder}/${f.name}`.toLowerCase().includes(st.filter.toLowerCase())) : free;
    const known = st.files.filter((f) => f.known);
    const target = h("select", { "aria-label": "Karte" }, st.cards.map((c) => h("option", { value: c.id }, c.label || c.data.teilename || `Karte ${c.id}`)));
    return h("div", { class: "panel" },
      h("div", { class: "panel-title" }, "2. Karten bilden"),
      h("div", { class: "panel-body" },
        h("div", { class: "row" },
          h("span", { class: "muted" }, "Vorschlag für nicht zugeordnete Dateien:"),
          h("button", { type: "button", onclick: () => suggest("folder") }, "Ein Ordner = eine Karte"),
          h("button", { type: "button", onclick: () => suggest("prefix") }, "Gleicher Dateiname-Anfang = eine Karte"),
          h("button", { type: "button", onclick: () => suggest("single") }, "Jede Datei = eigene Karte")),
        h("div", { class: "import-board section" },
          // left: files without card
          h("div", { class: "import-col" },
            h("div", { class: "import-col-head" },
              h("input", { type: "checkbox", "aria-label": "Alle", checked: visible.length > 0 && visible.every((f) => st.selected.has(f.id)), onchange: (e) => { for (const f of visible) e.target.checked ? st.selected.add(f.id) : st.selected.delete(f.id); draw(); } }),
              h("strong", {}, `Ohne Karte (${free.length})`),
              h("input", { class: "import-filter", "data-k": "filter", placeholder: "Filter …", value: st.filter, oninput: debounce((e) => { st.filter = e.target.value; draw(); }, 300) })),
            h("div", { class: "row import-actions" },
              h("button", { type: "button", disabled: !st.selected.size, onclick: () => { newCard(st.selected, stem(byId([...st.selected][0]).name)); st.selected.clear(); draw(); } }, `Neue Karte aus Auswahl (${st.selected.size})`),
              st.cards.length ? h("span", { class: "row" }, target, h("button", {
                type: "button",
                disabled: !st.selected.size,
                onclick: () => { const c = st.cards.find((x) => x.id === Number(target.value)); c.ops.push(...st.selected); st.selected.clear(); draw(); },
              }, "Hinzufügen")) : null,
              h("span", { class: "row" }, machineSelect(null, (v) => { if (v) { for (const id of st.selected) byId(id).machine_id = v; draw(); } }))),
            h("div", { class: "import-list" },
              visible.length ? visible.slice(0, 500).map(fileRow) : h("p", { class: "muted" }, st.files.length ? "Alle Dateien sind Karten zugeordnet." : "Noch keine Dateien gewählt."),
              visible.length > 500 ? h("p", { class: "muted" }, `… und ${visible.length - 500} weitere (Filter benutzen)`) : null),
            known.length ? h("details", { class: "section" }, h("summary", {}, `Schon importiert (${known.length}) – werden übersprungen`),
              known.slice(0, 200).map((f) => h("div", { class: "muted" }, `${f.name} → MDM ${f.known.mdm_number}`))) : null),
          // right: cards
          h("div", { class: "import-col" },
            h("div", { class: "import-col-head" }, h("strong", {}, `Karten (${st.cards.length})`)),
            h("div", { class: "import-list" },
              st.cards.length > MAX_CARDS_SHOWN ? h("p", { class: "muted" }, `Die ersten ${MAX_CARDS_SHOWN} von ${st.cards.length} Karten – alle werden importiert.`) : null,
              st.cards.length ? st.cards.slice(0, MAX_CARDS_SHOWN).map(cardBox) : h("p", { class: "muted" }, "Dateien links auswählen und „Neue Karte aus Auswahl“ – oder einen Vorschlag oben benutzen."))))));
  };

  const cardBox = (card) => h("div", { class: "import-card" },
    h("div", { class: "import-card-head" },
      card.article_id
        ? h("div", { class: "row" }, h("span", { class: "badge" }, "Bestehende Karte"), h("strong", {}, card.label),
          h("button", { type: "button", onclick: () => { card.article_id = null; card.label = ""; draw(); } }, "Als neue Karte"))
        : h("div", { class: "import-card-fields" },
          h("input", { value: card.data.teilename, placeholder: "Teilename *", "aria-label": "Teilename", oninput: (e) => { card.data.teilename = e.target.value; } }),
          h("input", { value: card.data.zeichnungsnummer, placeholder: "Zeichnungsnummer", "aria-label": "Zeichnungsnummer", oninput: (e) => { card.data.zeichnungsnummer = e.target.value; } }),
          customerSelect(card),
          h("button", { type: "button", onclick: () => pickExisting(card) }, "Zu bestehender Karte …")),
      h("button", { type: "button", class: "icon", title: "Karte auflösen", "aria-label": "Karte auflösen", onclick: () => removeCard(card) }, "×")),
    card.ops.map((id, i) => {
      const f = byId(id);
      return h("div", { class: "import-op" },
        h("span", { class: "muted import-opnr" }, String((i + 1) * 10)),
        h("button", { type: "button", class: "icon", title: "Nach oben", "aria-label": "Nach oben", disabled: i === 0, onclick: () => move(card, i, -1) }, "▲"),
        h("button", { type: "button", class: "icon", title: "Nach unten", "aria-label": "Nach unten", disabled: i === card.ops.length - 1, onclick: () => move(card, i, 1) }, "▼"),
        h("input", { value: f.title, "aria-label": "Prozessbeschreibung", title: fullPath(f) || f.rel, oninput: (e) => { f.title = e.target.value; } }),
        machineSelect(f.machine_id, (v) => { f.machine_id = v; }),
        h("button", { type: "button", class: "icon", title: "Aus Karte nehmen", "aria-label": "Aus Karte nehmen", onclick: () => unassign(card, id) }, "×"));
    }));

  const step3 = () => {
    const files = st.cards.reduce((n, c) => n + c.ops.length, 0);
    const existing = st.cards.filter((c) => c.article_id).length;
    return h("div", { class: "panel" },
      h("div", { class: "panel-title" }, "3. Importieren"),
      h("div", { class: "panel-body" },
        h("p", {}, `${st.cards.length} Karten (${st.cards.length - existing} neu, ${existing} bestehend) · ${files} Dateien → ${files} Operationen mit neuen MDM-Nummern`),
        h("label", { class: "check" }, h("input", { type: "checkbox", checked: st.copyNow, onchange: (e) => { st.copyNow = e.target.checked; } }),
          "Dateien gleich kopieren, wenn der Server sie erreicht (sonst kopiert der PDB Helper beim ersten Öffnen)"),
        h("div", { class: "row section" }, h("span", { class: "spacer" }),
          h("button", { type: "button", class: "primary", disabled: !files, onclick: (e) => runImport(e.currentTarget) }, "Importieren"))));
  };

  const resultPanel = () => {
    const r = st.result;
    return h("div", { class: "panel" },
      h("div", { class: "panel-title" }, "Import abgeschlossen"),
      h("div", { class: "panel-body" },
        h("p", {}, `${r.imported} Dateien importiert · ${r.copied} schon kopiert · ${r.pending} kopiert der PDB Helper beim ersten Öffnen${r.skipped ? ` · ${r.skipped} übersprungen (schon importiert)` : ""}`),
        h("ul", {}, r.cards.map((c, i) => h("li", {}, h("a", { href: `#/article/${c.article_id}` }, r.labels[i] || `Karte ${c.article_id}`),
          ` – ${c.operations.filter((o) => !o.skipped).map((o) => `MDM ${o.mdm_number}`).join(", ") || "nichts Neues"}`))),
        h("div", { class: "row section" }, h("span", { class: "spacer" }), h("button", { type: "button", onclick: () => { st.result = null; draw(); } }, "Weitere Dateien importieren"))));
  };

  const head = h("div", { class: "page-head" }, h("h1", {}, "Bestehende Dateien importieren"));
  const body = h("div", { class: "import-page" });
  // redraw everything from the state; keep scroll position and the field being typed in
  const draw = () => {
    const scroll = window.scrollY;
    const focused = document.activeElement?.dataset?.k;
    const caret = focused ? document.activeElement.selectionStart : null;
    if (st.result) clear(body, pendingPanel(pending), resultPanel());
    else clear(body, pendingPanel(pending), step1(), st.files.length ? step2() : null, st.cards.length ? step3() : null);
    window.scrollTo(0, scroll);
    const again = focused && body.querySelector(`[data-k="${focused}"]`);
    if (again) { again.focus(); if (caret != null) again.setSelectionRange(caret, caret); }
  };
  if (!active.length) {
    clear(root, head, h("div", { class: "panel panel-body" }, "Noch keine Maschinen angelegt", " – ", h("a", { href: "#/admin/machines" }, "Maschinen")));
    return;
  }
  clear(root, head, body);
  draw();
}
