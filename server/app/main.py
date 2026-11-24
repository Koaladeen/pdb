"""PDB server: file management for Mastercam files (MDM replacement).

Run:  uvicorn app.main:app --host 0.0.0.0 --port 8000
API docs at http://<server>:8000/docs
"""
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles

from .config import Config, load_config
from .db import Database
from . import backup, pictures
from .routers import admin, articles, auth, catalog, customers, downloads, file_import, imports, locks, setup
from .routers import pictures as pictures_router
from .seed import seed


def create_app(config: Config | None = None) -> FastAPI:
    config = config or load_config()
    db = Database(config.database_url)
    pictures.configure(config.data_dir)
    downloads.configure(config.data_dir)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        db.create_all()
        with db.sessionmaker() as s:
            seed(s, config)
        stop_backups = backup.start(config.database_url, config.data_dir)
        yield
        if stop_backups:
            stop_backups.set()
        db.engine.dispose()

    app = FastAPI(title="PDB - Dateiverwaltung", version=(Path(__file__).resolve().parents[2] / "VERSION").read_text().strip() if (Path(__file__).resolve().parents[2] / "VERSION").exists() else "dev", lifespan=lifespan)
    app.state.config = config
    app.state.db = db
    for r in (setup, auth, catalog, customers, articles, locks, pictures_router, imports, file_import, downloads, admin):
        app.include_router(r.router)

    @app.get("/health", tags=["meta"])
    def health():
        return {"ok": True}

    # Web UI: plain HTML/JS modules in app/static, no build step, works offline.
    app.mount("/app", StaticFiles(directory=Path(__file__).parent / "static", html=True), name="ui")

    @app.get("/", include_in_schema=False)
    def root():
        return RedirectResponse("/app/")

    return app


app = create_app()
