"""Server settings, read from environment variables (prefix PDB_)."""
import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Config:
    database_url: str
    lock_timeout_minutes: int
    token_hours: int
    initial_admin_user: str
    initial_admin_password: str
    data_dir: str = "./data"  # operation pictures and other files the server keeps


def load_config() -> Config:
    return Config(
        data_dir=os.environ.get("PDB_DATA_DIR", "./data"),
        database_url=os.environ.get("PDB_DATABASE_URL", "sqlite:///./pdb.sqlite3"),
        lock_timeout_minutes=int(os.environ.get("PDB_LOCK_TIMEOUT_MINUTES", "10")),
        token_hours=int(os.environ.get("PDB_TOKEN_HOURS", "12")),
        initial_admin_user=os.environ.get("PDB_ADMIN_USER", "admin"),
        initial_admin_password=os.environ.get("PDB_ADMIN_PASSWORD", ""),  # empty: setup wizard
    )
