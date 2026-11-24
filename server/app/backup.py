"""Automatic database backups (SQLite only; Postgres has its own tools).

At start and then every PDB_BACKUP_HOURS (default 24) a consistent copy of the database is
written to <PDB_DATA_DIR>/backups/pdb-YYYYMMDD-HHMMSS.sqlite3; the newest PDB_BACKUP_KEEP
(default 14) are kept. A copy is skipped if the last one is younger than the interval, so
frequent restarts don't fill the folder. PDB_BACKUP_HOURS=0 switches it off.
Restore: stop PDB, replace the database file with a backup, start PDB.
"""
import logging
import os
import sqlite3
import threading
import time
from datetime import datetime
from pathlib import Path

log = logging.getLogger("pdb.backup")


def sqlite_path(database_url: str) -> Path | None:
    if not database_url.startswith("sqlite:///") or ":memory:" in database_url:
        return None
    return Path(database_url[len("sqlite:///"):])


def backups(folder: Path) -> list[Path]:
    return sorted(folder.glob("pdb-*.sqlite3"))


def backup_now(db_file: Path, folder: Path, keep: int) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / f"pdb-{datetime.now():%Y%m%d-%H%M%S}.sqlite3"
    src = sqlite3.connect(db_file)
    try:
        dst = sqlite3.connect(target)
        try:
            src.backup(dst)  # consistent even while PDB writes (WAL)
        finally:
            dst.close()
    finally:
        src.close()
    for old in backups(folder)[:-keep] if keep > 0 else []:
        old.unlink(missing_ok=True)
    return target


def start(database_url: str, data_dir: str) -> threading.Event | None:
    """Starts the backup loop in the background. Returns an event that stops it."""
    db_file = sqlite_path(database_url)
    hours = float(os.environ.get("PDB_BACKUP_HOURS", "24"))
    keep = int(os.environ.get("PDB_BACKUP_KEEP", "14"))
    if db_file is None or hours <= 0:
        return None
    folder = Path(data_dir) / "backups"
    interval = hours * 3600
    stop = threading.Event()

    def loop():
        while not stop.is_set():
            try:
                last = backups(folder)
                age = time.time() - last[-1].stat().st_mtime if last else None
                if db_file.exists() and (age is None or age >= interval):
                    log.info("database backup: %s", backup_now(db_file, folder, keep))
                    age = 0
                wait = interval - (age or 0)
            except Exception as e:  # never take the server down for a backup
                log.warning("database backup failed: %s", e)
                wait = 3600
            stop.wait(max(60, wait))

    threading.Thread(target=loop, name="pdb-backup", daemon=True).start()
    return stop
