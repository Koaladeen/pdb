// Light / dark mode button in the top bar. The setting itself lives in the user's appearance
// preferences (prefs.js) and follows the user to every PC.
import { prefs, setPrefs } from "./prefs.js";
import { h } from "./ui.js";

const MODES = [
  ["auto", "◐", "Design: automatisch (wie Windows)"],
  ["light", "☀", "Design: hell"],
  ["dark", "☾", "Design: dunkel"],
];

// Button for the top bar: cycles auto -> light -> dark.
export function themeButton() {
  const btn = h("button", { class: "small theme-btn" });
  const show = () => {
    const [, icon, label] = MODES.find(([m]) => m === prefs().theme) || MODES[0];
    btn.textContent = icon;
    btn.title = `${label} – klicken zum Wechseln`;
    btn.setAttribute("aria-label", label);
  };
  btn.addEventListener("click", () => {
    const i = MODES.findIndex(([m]) => m === prefs().theme);
    setPrefs({ theme: MODES[(i + 1) % MODES.length][0] });
  });
  window.addEventListener("pdb:prefs", show);
  show();
  return btn;
}
