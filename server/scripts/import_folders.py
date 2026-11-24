"""Read existing MDM operation folders back into PDB.

Runs on a Windows PC that sees the share (the server cannot). For every
<Mastercam main folder>\\<machine folder>\\MDM<number>\\ it reads the header of the posted
<number>.nc (DB-NR, TEILENAME, ZCH-NR, OP-NAME, PROGRAMMIERER), groups operations into parts
by drawing number + part name, and uploads the picture (Pic<number>.png.cam, else .png).

Dry run by default - shows what would happen and changes nothing. Add --apply to write.
MDM numbers that already exist in PDB are skipped, so running it twice is harmless.

  python scripts/import_folders.py --server http://pdb-server:8000 --user admin
  python scripts/import_folders.py --server ... --user admin --machine DMU50 --apply

Only standard library; asks for the password itself (never pass it on the command line).
"""
import argparse
import getpass
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

HEADER = re.compile(r"(DB-NR\.?|TEILENAME|ZCH-NR|OP-NAME|PROGRAMMIERER)\s*:\s*(.*?)\s*\)?\s*$")
FOLDER = re.compile(r"^MDM(\d+)$", re.IGNORECASE)


class Api:
    def __init__(self, base: str):
        self.base = base.rstrip("/")
        self.token = None

    def call(self, method, path, body=None, raw: bytes | None = None):
        data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
        req = urllib.request.Request(self.base + path, method=method, data=data)
        req.add_header("Content-Type", "application/octet-stream" if raw is not None else "application/json")
        if self.token:
            req.add_header("Authorization", f"Bearer {self.token}")
        try:
            with urllib.request.urlopen(req, timeout=30) as res:
                return json.loads(res.read() or b"null")
        except urllib.error.HTTPError as e:
            raise SystemExit(f"{method} {path}: {e.code} {e.read().decode(errors='replace')}") from None


def read_header(nc: Path) -> dict:
    """First lines of a posted program; Heidenhain (; KEY : value) or FANUC ((KEY : value))."""
    found = {}
    with nc.open(encoding="latin-1", errors="replace") as f:
        for _, line in zip(range(40), f):
            m = HEADER.search(line)
            if m and m.group(1).rstrip(".") not in found:
                found[m.group(1).rstrip(".")] = m.group(2).strip()
    return found


def best_picture(folder: Path, number: str) -> Path | None:
    pics = [p for p in folder.iterdir() if re.fullmatch(rf"pic-?{number}\.png(\.cam)?", p.name, re.IGNORECASE)]
    cam = [p for p in pics if p.name.lower().endswith(".cam")]
    choice = cam or pics
    return max(choice, key=lambda p: p.stat().st_mtime) if choice else None


def login(api: Api, user: str) -> None:
    """In a terminal: hidden prompt. When piped (automation): first line of stdin."""
    password = getpass.getpass(f"Passwort für {user}: ") if sys.stdin.isatty() else sys.stdin.readline().rstrip("\r\n")
    api.token = api.call("POST", "/auth/login", {"username": user, "password": password, "pc_name": "import"})["token"]


def import_folders(api: Api, root: Path, only: list[str] | None, apply: bool) -> dict:
    """Scan root/<machine folder>/MDM<nr>/ and record each operation. Returns counts."""
    machines = {m["mcam_folder"].lower(): m for m in api.call("GET", "/admin/machines")}
    users = {u["display_name"].strip().lower(): u["id"] for u in api.call("GET", "/users") if u.get("active", True)}
    if not root.is_dir():
        sys.exit(f"Ordner nicht gefunden: {root}")
    # Dry run writes nothing, so the server cannot see parts planned earlier in this run;
    # count them here so the preview groups and numbers like the real run will.
    planned: dict[tuple[str, str], int] = {}

    print(f"{'IMPORT' if apply else 'PROBELAUF (nichts wird geändert, --apply zum Schreiben)'}: {root}\n")
    stats = {"neu": 0, "vorhanden": 0, "ohne NC": 0, "Bilder": 0}
    for mdir in sorted(p for p in root.iterdir() if p.is_dir()):
        if only and mdir.name not in only:
            continue
        machine = machines.get(mdir.name.lower())
        op_dirs = sorted((p for p in mdir.iterdir() if p.is_dir() and FOLDER.match(p.name)), key=lambda p: int(FOLDER.match(p.name)[1]))
        if not op_dirs:
            continue
        if machine is None:
            print(f"!! {mdir.name}: keine Maschine mit diesem Ordner in PDB - übersprungen ({len(op_dirs)} Ordner)")
            continue
        print(f"== {mdir.name} -> {machine['name']}")
        for odir in op_dirs:
            n = int(FOLDER.match(odir.name)[1])
            if n < 1:
                continue
            num = f"{n:08d}"
            nc = odir / f"{num}.nc"
            head = read_header(nc) if nc.exists() else {}
            article = {"teilename": head.get("TEILENAME") or f"Unbekannt (MDM{n})"}
            if head.get("ZCH-NR"):
                article["zeichnungsnummer"] = head["ZCH-NR"]
            operation = {"cam_system": "Mastercam"}
            if head.get("OP-NAME"):
                operation["prozessbeschreibung"] = head["OP-NAME"]
            prog = head.get("PROGRAMMIERER", "")
            if prog and prog.lower() in users:
                operation["cam_programmierer"] = users[prog.lower()]
            elif prog:
                operation["kommentar"] = f"Programmierer laut NC-Kopf: {prog}"
            if not head:
                stats["ohne NC"] += 1

            res = api.call("POST", "/admin/import/operation", {
                "machine_id": machine["id"], "mdm_number": n, "article": article,
                "operation": operation, "dry_run": not apply,
            })
            pic = best_picture(odir, num)
            if res["result"] == "exists":
                stats["vorhanden"] += 1
                print(f"   MDM{n}: schon in PDB")
                continue
            stats["neu"] += 1
            key = (article.get("zeichnungsnummer", "").lower(), article["teilename"].lower())
            earlier = planned.get(key, 0) if not apply else 0
            planned[key] = planned.get(key, 0) + 1
            new_part = res["article_created"] and earlier == 0
            op_number = res["op_number"] + 10 * earlier
            part = f"{article.get('zeichnungsnummer', '–')} / {article['teilename']}"
            print(f"   MDM{n}: {res['result']} · Teil {part}{' (neu)' if new_part else ''} · Nr. {op_number}"
                  f" · {operation.get('prozessbeschreibung', '?')} · {prog or '–'}{' · Bild ' + pic.name if pic else ''}")
            if apply and pic:
                api.call("PUT", f"/operations/{res['operation_id']}/picture", raw=pic.read_bytes())
                stats["Bilder"] += 1

    print("\n" + ", ".join(f"{k}: {v}" for k, v in stats.items()))
    if not apply:
        print("Probelauf - nichts geändert. Mit --apply ausführen, um zu schreiben.")
    return stats


def utf8_console() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # umlauts in any console


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--server", required=True)
    ap.add_argument("--user", required=True, help="admin user")
    ap.add_argument("--root", help="Mastercam main folder (default: the server's setting)")
    ap.add_argument("--machine", action="append", help="only this machine folder (repeatable)")
    ap.add_argument("--apply", action="store_true", help="really write (default: dry run)")
    args = ap.parse_args()
    utf8_console()
    api = Api(args.server)
    login(api, args.user)
    root = Path(args.root or api.call("GET", "/admin/settings")["mcam_root"])
    import_folders(api, root, args.machine, args.apply)


if __name__ == "__main__":
    main()
