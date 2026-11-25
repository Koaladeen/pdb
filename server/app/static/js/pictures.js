// Operation pictures (Mastercam screenshots). Loaded once per picture version and reused.
import { api, files } from "./api.js";
import { h } from "./ui.js";

const cache = new Map(); // "id@picture_at" -> Promise<objectURL|null>

function url(op) {
  if (!op.picture_at) return Promise.resolve(null);
  const key = `${op.id}@${op.picture_at}`;
  if (!cache.has(key)) {
    cache.set(key, files.getBlob(`/operations/${op.id}/picture?v=${encodeURIComponent(op.picture_at)}`)
      .then((blob) => URL.createObjectURL(blob))
      .catch(() => null));
  }
  return cache.get(key);
}

// <img> that fills itself in; empty placeholder when the operation has no picture.
export function pictureImg(op, cls = "") {
  const img = h("img", { class: cls, alt: op.picture_at ? `Bild PDB-Nr. ${op.mdm_number_text}` : "", loading: "lazy" });
  if (!op.picture_at) {
    img.hidden = true;
    return img;
  }
  url(op).then((u) => { if (u) img.src = u; else img.hidden = true; });
  return img;
}

export async function uploadPicture(operationId, blob) {
  return files.put(`/operations/${operationId}/picture`, blob);
}

export async function deletePicture(operationId) {
  return api.del(`/operations/${operationId}/picture`);
}
