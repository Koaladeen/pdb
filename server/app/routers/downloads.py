"""Downloads for the PCs: PDB Helper and the Mastercam bridge, plus version.json that the
helper checks for updates. Files live in <data_dir>/downloads and are put there by
helper/release.sh. No login needed: the page links "Helper installieren" before the helper
exists, and the files contain no data."""
import json
import re
from pathlib import Path

from fastapi import APIRouter, HTTPException
from fastapi.responses import FileResponse, JSONResponse

router = APIRouter(prefix="/downloads", tags=["downloads"])

_dir: Path | None = None
_SAFE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$")


def configure(data_dir: str) -> None:
    global _dir
    _dir = Path(data_dir) / "downloads"
    _dir.mkdir(parents=True, exist_ok=True)


def _file(name: str) -> Path:
    if _dir is None or not _SAFE.match(name):
        raise HTTPException(404, "Not found")
    path = _dir / name
    if not path.is_file():
        raise HTTPException(404, "Not found")
    return path


@router.get("/version.json")
def version():
    """{"helper": {"version", "file", "sha256"}, "bridge": {"net48"|"net10": {...}},
    "shared_addins": {"2025": "M:\\Mastercam 2025 shared\\Add-Ins", ...}}"""
    try:
        data = json.loads(_file("version.json").read_text(encoding="utf-8"))
    except HTTPException:
        return JSONResponse({"helper": None, "bridge": {}, "shared_addins": {}})
    return JSONResponse(data, headers={"Cache-Control": "no-store"})


@router.get("/{name}")
def download(name: str):
    path = _file(name)
    return FileResponse(path, filename=name, headers={"Cache-Control": "no-store"})
