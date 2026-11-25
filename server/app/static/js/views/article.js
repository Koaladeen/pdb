// Part card: blocks for card fields, picture and Prozessdaten (operations); each user can
// rearrange them (layout.js).
import { api } from "../api.js";
import { fieldForm, fieldList, operationCell } from "../fields.js";
import { openInMastercam } from "../helper.js";
import { pictureImg } from "../pictures.js";
import { closeCard, rememberCard } from "../workspace.js";
import { blockLayout } from "../layout.js";
import { prefs } from "../prefs.js";
import { isAdmin, store } from "../store.js";
import { attempt, badge, clear, confirmDialog, formatDateTime, h, modal, table } from "../ui.js";
import { newOperationDialog, operationDialog } from "./operation-dialogs.js";

export async function render(root, id) {
  let article;
  try {
    article = await api.get(`/articles/${id}`);
  } catch (err) {
    if (err.status === 404) {
      closeCard(Number(id)); // deleted meanwhile: drop it from the sidebar
      clear(root, h("div", { class: "panel panel-body" }, "Dieser Artikel existiert nicht mehr."));
      return;
    }
    throw err;
  }
  rememberCard(article);
  const reload = () => render(root, id);
  const d = article.data;
  const title = [d.zeichnungsnummer, d.teilename].filter(Boolean).join(" ♦ ") || `Artikel ${article.id}`;

  // Big picture: first operation with a picture; a row's thumbnail switches it.
  const withPicture = article.operations.filter((op) => op.picture_at);
  const preview = h("figure", { class: "card-preview" });
  const showPreview = (op) => {
    clear(preview,
      op ? pictureImg(op) : h("div", { class: "no-picture muted" }, "Kein Bild"),
      op ? h("figcaption", { class: "muted" }, `${op.data.prozessbeschreibung || `Nr. ${op.op_number}`} · PDB-Nr. ${op.mdm_number_text}`) : null);
  };
  showPreview(withPicture[0]);

  const opColumns = [
    {
      label: "",
      value: (op) => (op.picture_at
        ? h("span", { class: "thumb", title: "Bild oben anzeigen", onclick: (e) => { e.stopPropagation(); showPreview(op); } }, pictureImg(op))
        : ""),
    },
    ...store.fields.operation
      .filter((fd) => fd.show_in_list)
      .map((fd) => ({ label: fd.label, class: fd.system ? "num" : "", value: (op) => operationCell(fd, op) })),
    { label: "V", class: "num", value: (op) => String(op.current_revision) },
    {
      label: "",
      value: (op) => h("button", {
        class: "small",
        title: "In Mastercam öffnen",
        onclick: (e) => { e.stopPropagation(); openInMastercam(op.id).then(reload); },
      }, "Öffnen"),
    },
    {
      label: "",
      value: (op) => op.lock
        ? badge(`🔒 ${op.lock.user}`, op.lock.stale ? "warn" : "")
        : "",
    },
  ];

  // Blocks of the card page; each user can rearrange them ("Layout anpassen", saved per user).
  const blocks = [
    {
      id: "fields",
      title: "Kartenfelder",
      span: 4,
      el: h("div", { class: "panel" },
        h("div", { class: "panel-body" }, h("div", { class: "card-fields" }, fieldList("article", d))),
        h("div", { class: "panel-body muted", style: { paddingTop: 0, fontSize: "12px" } },
          `Angelegt ${formatDateTime(article.created_at)} · geändert ${formatDateTime(article.updated_at)}`)),
    },
    prefs().pictures ? { id: "picture", title: "Bild", span: 2, el: h("div", { class: "panel" }, h("div", { class: "panel-body" }, preview)) } : null,
    {
      id: "operations",
      title: "Prozessdaten",
      span: 6,
      el: h("div", { class: "panel" },
        h("div", { class: "panel-title" }, "Prozessdaten",
          h("button", { class: "small", onclick: () => newOperationDialog(article, reload) }, "+ Neue Operation")),
        table({
          columns: opColumns,
          rows: article.operations,
          onRowClick: (op) => operationDialog(op.id, reload),
          empty: "Noch keine Operationen",
        })),
    },
  ].filter(Boolean);
  const layout = blockLayout("article", blocks);

  clear(
    root,
    h(
      "div",
      { class: "page-head" },
      h("div", {}, h("a", { href: "#/search" }, "‹ Suche"), h("h1", {}, title)),
      h(
        "div",
        { class: "row" },
        h("button", { onclick: () => editArticle(article, reload) }, "Bearbeiten"),
        layout.button,
        isAdmin() && article.operations.length === 0
          ? h("button", { class: "danger", onclick: () => deleteArticle(article) }, "Löschen")
          : null,
        h("button", { title: "Karte schließen", onclick: () => closeCard(article.id) }, "Schließen"),
      ),
    ),
    layout.el,
  );
}

function editArticle(article, reload) {
  const form = fieldForm("article", article.data);
  modal({
    title: "Artikel bearbeiten",
    body: form.el,
    actions: [
      { label: "Abbrechen" },
      {
        label: "Speichern",
        primary: true,
        onClick: async () => {
          const changes = form.values();
          if (!Object.keys(changes).length) return;
          const ok = await attempt(() => api.patch(`/articles/${article.id}`, { data: changes }), "Gespeichert");
          if (!ok) return false;
          reload();
        },
      },
    ],
  });
}

async function deleteArticle(article) {
  if (!(await confirmDialog("Artikel löschen", "Diesen Artikel endgültig löschen?", "Löschen", true))) return;
  const ok = await attempt(() => api.del(`/articles/${article.id}`), "Gelöscht");
  if (ok) {
    closeCard(article.id);
    location.hash = "#/search";
  }
}
