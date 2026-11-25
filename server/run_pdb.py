"""Starts the PDB server of an installation made by PDB-Setup.exe (no console, settings from install.json).

    pythonw.exe run_pdb.py <path to install.json>

install.json (written by the installer): DataDir, Port, Network, SuggestMcam, SuggestNc.
Database and files live in DataDir; output goes to DataDir/server.log; the process id to
DataDir/server.pid (PDB.exe --stop uses it).
"""
import json
import os
import sys


def main() -> None:
    cfg = json.load(open(sys.argv[1], encoding="utf-8-sig"))
    # portable version: paths relative to the folder of install.json (wherever it was copied to)
    base = os.path.dirname(os.path.abspath(sys.argv[1]))
    for key in ("DataDir", "SuggestMcam", "SuggestNc"):
        if cfg.get(key):
            cfg[key] = os.path.join(base, cfg[key])  # an absolute path stays as it is
    data = cfg["DataDir"]
    os.makedirs(data, exist_ok=True)

    log_path = os.path.join(data, "server.log")
    if os.path.exists(log_path) and os.path.getsize(log_path) > 5_000_000:
        os.replace(log_path, log_path + ".1")
    log = open(log_path, "a", encoding="utf-8", buffering=1)
    sys.stdout = sys.stderr = log  # pythonw has no console

    os.environ.setdefault("PDB_DATABASE_URL", "sqlite:///" + os.path.join(data, "pdb.sqlite3").replace("\\", "/"))
    os.environ.setdefault("PDB_DATA_DIR", os.path.join(data, "files"))
    if cfg.get("SuggestMcam"):
        os.environ.setdefault("PDB_SUGGEST_MCAM_ROOT", cfg["SuggestMcam"])
    if cfg.get("SuggestNc"):
        os.environ.setdefault("PDB_SUGGEST_NC_ROOT", cfg["SuggestNc"])
    with open(os.path.join(data, "server.pid"), "w") as f:
        f.write(str(os.getpid()))

    import uvicorn

    host = "0.0.0.0" if cfg.get("Network") else "127.0.0.1"
    print(f"PDB starting on {host}:{cfg.get('Port', 8000)}", flush=True)
    uvicorn.run("app.main:app", host=host, port=int(cfg.get("Port", 8000)), log_level="warning")


if __name__ == "__main__":
    main()
