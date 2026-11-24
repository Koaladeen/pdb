// Talks to the PDB Helper tray app on this PC (127.0.0.1). The helper checks the file out with
// its own login, opens it in Mastercam and watches saving, closing and NC posting.
import { currentToken } from "./api.js";
import { confirmDialog, toast } from "./ui.js";

const HELPER = "http://127.0.0.1:5017";

export async function helperStatus() {
  try {
    const res = await fetch(`${HELPER}/status`, { signal: AbortSignal.timeout(1500) });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

async function post(path, body) {
  const res = await fetch(`${HELPER}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(30000),
  });
  let data = {};
  try { data = await res.json(); } catch { /* empty */ }
  return { status: res.status, data };
}

// Fallback when the browser cannot reach 127.0.0.1: a pdb:// link starts the helper via Windows.
function openByLink(operationId, readOnly) {
  location.href = `pdb://open/${operationId}${readOnly ? "/readonly" : ""}`;
}

// Machine change through the helper, so the files move too (and, if the file is open in
// Mastercam, the machine definition is switched there). Returns {status, message} or null
// when the helper is not reachable.
export async function changeMachineViaHelper(operationId, machineId) {
  try {
    const { status, data } = await post("/machine", { operation_id: operationId, machine_id: machineId, token: currentToken() });
    return { status, message: data.message || `Helper-Fehler ${status}` };
  } catch {
    return null;
  }
}

export async function openInMastercam(operationId, readOnly = false) {
  let result;
  try {
    // The helper only accepts this from the PDB page (origin check) and asks once per new address.
    result = await post("/open", { operation_id: operationId, read_only: readOnly, token: currentToken() });
  } catch {
    toast("PDB Helper nicht erreichbar – öffne über Link. Falls nichts passiert: PDB Helper starten.", "error");
    openByLink(operationId, readOnly);
    return;
  }
  const { status, data } = result;
  if (status === 200) {
    toast(data.message || "Geöffnet", "ok");
  } else if (status === 409) {
    const who = data.lock ? `${data.lock.user} an ${data.lock.pc_name}` : "jemand anderem";
    if (await confirmDialog("Datei gesperrt", `Die Datei ist von ${who} geöffnet. Schreibgeschützt öffnen?`, "Nur lesen")) {
      await openInMastercam(operationId, true);
    }
  } else {
    toast(data.message || `Helper-Fehler ${status}`, "error");
  }
}
