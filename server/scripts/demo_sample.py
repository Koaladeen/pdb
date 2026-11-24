"""Sets up a NEW, empty PDB as a demo with neutral sample data (for trying PDB, e.g. try-local.ps1 -Demo).

Runs the first setup as the built-in admin, then adds users without passwords, customers, parts and
operations. Everything is made up. Usage (server running on a fresh database):
    PDB_DEMO_ADMIN_PASSWORD=<new admin password> python scripts/demo_sample.py [http://127.0.0.1:8000]
Optional: PDB_DEMO_MCAM_ROOT / PDB_DEMO_NC_ROOT (folders as this PC sees them).
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8000").rstrip("/")

MACHINES = [
    {"name": "DMU 50", "mcam_folder": "DMU50", "nc_folder": "DMU50", "nc_naming": "mdm", "nc_extension": ".H"},
    {"name": "Haas VF-2", "mcam_folder": "Haas_VF2", "nc_folder": "Haas_VF2", "nc_naming": "nc", "nc_extension": ".NC"},
    {"name": "Mazak Integrex i-200", "mcam_folder": "Mazak_i200", "nc_folder": "Mazak_i200", "nc_naming": "nc", "nc_extension": ".EIA"},
    {"name": "Hermle C 42", "mcam_folder": "Hermle_C42", "nc_folder": "Hermle_C42", "nc_naming": "mdm", "nc_extension": ".H"},
]
USERS = [("anna", "Anna Programmer"), ("tom", "Tom Operator"), ("lina", "Lina CAM")]
CUSTOMERS = [
    {"name": "Nordic Robotics", "number": "K-1001", "contact": "J. Berg"},
    {"name": "Alpine Medical", "number": "K-1002"},
    {"name": "Baltic Marine Parts", "number": "K-1003", "contact": "R. Kazlauskas"},
    {"name": "Internal", "number": "K-0000"},
]
# part data, operations: (title, machine index, programmer index, status)
PARTS = [
    ({"teilename": "Gripper Base Plate", "zeichnungsnummer": "NR-20431", "index_version": "B", "artikelnummer": "100431",
      "kunde": "Nordic Robotics", "stueckzahl": 40, "status": "Freigegeben", "rohteil_material": "EN AW-7075",
      "rohteil_masse": "160x120x25", "auftragsnummer": "A-5101", "startdatum_auftrag": "2026-11-02", "lieferdatum_auftrag": "2026-12-04"},
     [("1 OP Roughing", 0, 0, "Freigegeben"), ("2 OP Finishing", 0, 0, "Freigegeben")]),
    ({"teilename": "Implant Holder", "zeichnungsnummer": "AM-7782", "index_version": "A", "artikelnummer": "200782",
      "kunde": "Alpine Medical", "stueckzahl": 250, "status": "Freigegeben", "rohteil_material": "1.4404", "rohteil_art": "Round bar",
      "rohteil_masse": "Ø30x80", "notiz": "Deburr all edges, no marking on functional surfaces."},
     [("1 OP Turning + milling", 2, 2, "Freigegeben")]),
    ({"teilename": "Propeller Hub Flange", "zeichnungsnummer": "BMP-3310", "index_version": "C", "artikelnummer": "303310",
      "kunde": "Baltic Marine Parts", "stueckzahl": 12, "status": "Gesperrt", "projektnummer": "P-2026-044",
      "rohteil_material": "CuAl10Ni5Fe4", "notiz": "Drawing revision C pending – do not produce."},
     [("1 OP Front side", 3, 0, "Gesperrt"), ("2 OP Back side", 3, 0, "Gesperrt")]),
    ({"teilename": "Clamping Jaw 80mm", "zeichnungsnummer": "INT-0815", "artikelnummer": "900815", "kunde": "Internal",
      "stueckzahl": 8, "status": "Freigegeben", "rohteil_material": "1.2312", "rohteil_masse": "80x40x30"},
     [("1 OP 6-fold clamping", 1, 1, "Freigegeben")]),
    ({"teilename": "Sensor Bracket", "zeichnungsnummer": "NR-20502", "index_version": "A", "kunde": "Nordic Robotics",
      "stueckzahl": 100, "status": "Freigegeben", "rohteil_material": "EN AW-6082"},
     [("1 OP Complete", 1, 2, "Freigegeben")]),
]


def call(method, path, token=None, body=None):
    req = urllib.request.Request(BASE + path, method=method, data=None if body is None else json.dumps(body).encode())
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req) as res:
            return json.loads(res.read() or b"null")
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {path} failed: {e.code} {e.read().decode()}") from None


def main():
    password = os.environ.get("PDB_DEMO_ADMIN_PASSWORD", "")
    if len(password) < 6 or password == "admin":
        raise SystemExit("PDB_DEMO_ADMIN_PASSWORD: at least 6 characters, not 'admin'")
    if not call("GET", "/setup/status")["needed"]:
        raise SystemExit("This PDB is already set up - demo data only goes into a new, empty database.")
    first = call("POST", "/auth/login", body={"username": "admin", "password": "admin", "pc_name": "demo"})["token"]
    home = os.path.expanduser("~")
    token = call("POST", "/setup", first, {
        "company": "PDB Demo",
        "admin": {"username": "admin", "display_name": "Administrator", "password": password},
        "folders": {"mcam_root": os.environ.get("PDB_DEMO_MCAM_ROOT") or os.path.join(home, "PDB-Test", "Mastercam"),
                    "nc_root": os.environ.get("PDB_DEMO_NC_ROOT") or os.path.join(home, "PDB-Test", "NC")},
        "fields": "full",
        "machines": MACHINES,
        "mdm_start": 1000,
    })["token"]
    machine_ids = [m["id"] for m in call("GET", "/admin/machines", token)]
    users = [call("POST", "/admin/users", token, {"username": u, "display_name": d, "machine_ids": machine_ids})["id"] for u, d in USERS]
    customers = {c["name"]: call("POST", "/customers", token, c)["id"] for c in CUSTOMERS}
    ops = 0
    for data, operations in PARTS:
        part = call("POST", "/articles", token, {"data": {**data, "kunde": customers[data["kunde"]]}})
        for title, machine, programmer, status in operations:
            call("POST", f"/articles/{part['id']}/operations", token, {
                "machine_id": machine_ids[machine],
                "data": {"prozessbeschreibung": title, "cam_programmierer": users[programmer], "operations_status": status,
                         "material": data.get("rohteil_material", "")},
            })
            ops += 1
    print(f"Demo ready: {len(MACHINES)} machines, {len(USERS)} users without password, "
          f"{len(CUSTOMERS)} customers, {len(PARTS)} parts, {ops} operations.")


if __name__ == "__main__":
    main()
