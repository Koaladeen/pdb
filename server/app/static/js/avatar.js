// Profile pictures: round picture, or initials when there is none.
import { h } from "./ui.js";

export function avatar(user, size = 32) {
  const style = { width: `${size}px`, height: `${size}px`, fontSize: `${Math.round(size * 0.4)}px` };
  if (user && user.avatar_at) {
    return h("img", {
      class: "avatar",
      style,
      alt: "",
      src: `/users/${user.id}/avatar?v=${encodeURIComponent(user.avatar_at)}`,
    });
  }
  const name = (user && (user.display_name || user.username)) || "?";
  const initials = name.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join("");
  return h("span", { class: "avatar initials", style, "aria-hidden": "true" }, initials);
}

// Shrinks a chosen photo to a square of `max` pixels before upload (phones make huge pictures).
export async function squareImage(file, max = 256) {
  const bitmap = await createImageBitmap(file);
  const side = Math.min(bitmap.width, bitmap.height);
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = Math.min(max, side);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.88));
}
