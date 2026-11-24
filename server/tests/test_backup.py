"""Automatic SQLite backups."""
import sqlite3

from app import backup


def test_backup_copies_and_keeps_the_newest(tmp_path):
    db = tmp_path / "pdb.sqlite3"
    con = sqlite3.connect(db)
    con.execute("create table t (x)")
    con.execute("insert into t values (42)")
    con.commit()
    folder = tmp_path / "backups"
    for i in range(4):
        (folder).mkdir(exist_ok=True)
        (folder / f"pdb-2020010{i}-000000.sqlite3").write_bytes(b"old")
    made = backup.backup_now(db, folder, keep=3)
    assert sqlite3.connect(made).execute("select x from t").fetchone() == (42,)
    names = [p.name for p in backup.backups(folder)]
    assert len(names) == 3 and made.name in names and "pdb-20200100-000000.sqlite3" not in names
    assert backup.sqlite_path("sqlite:///C:/x/pdb.sqlite3").name == "pdb.sqlite3"
    assert backup.sqlite_path("postgresql://x") is None
