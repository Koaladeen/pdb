"""Taking over existing Mastercam files from anywhere (import page in the web UI, admin only).

The page lists files (from a folder picked in the browser - only names, sizes and dates, nothing is
uploaded - or from a pasted list of paths) and lets the admin group them into cards. Each file
becomes an operation with a new PDB number. The original is copied into the operation folder by the
server right away if it can reach the path (e.g. a server on the same PC), otherwise by the PDB
Helper the first time the operation is opened. Originals are never changed or moved.
"""
import ntpath
import os
import shutil
from typing import Any

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from ..deps import current_user, get_db, require_admin
from ..models import Article, ImportedFile, Machine, Operation, User, utcnow
from ..services import audit, field_defs, merge_data, next_mdm_number, next_nc_number, operation_paths

router = APIRouter(tags=["admin"])


class ImportFileOp(BaseModel):
    source_path: str = Field(min_length=3, max_length=1000)
    machine_id: int
    title: str = Field(default="", max_length=200)
    size: int | None = None
    modified: str = Field(default="", max_length=40)


class ImportCard(BaseModel):
    article_id: int | None = None  # add to an existing card ...
    article: dict[str, Any] = {}  # ... or create a new one with these values
    operations: list[ImportFileOp] = Field(min_length=1, max_length=500)


class ImportFilesIn(BaseModel):
    cards: list[ImportCard] = Field(min_length=1, max_length=5000)
    copy_now: bool = True


class PathsIn(BaseModel):
    paths: list[str] = Field(max_length=50000)


class CopiedIn(BaseModel):
    pc_name: str = Field(default="", max_length=100)


def known_files(s: Session, paths: list[str]) -> dict[str, ImportedFile]:
    """Already imported source paths, keyed lower-case (Windows paths ignore case)."""
    lowered = sorted({p.strip().lower() for p in paths})
    found: dict[str, ImportedFile] = {}
    for i in range(0, len(lowered), 500):
        q = (select(ImportedFile)
             .join(Operation, Operation.id == ImportedFile.operation_id)
             .where(func.lower(ImportedFile.source_path).in_(lowered[i:i + 500])))
        for imp in s.scalars(q):
            found[imp.source_path.lower()] = imp
    return found


def server_copy(s: Session, imp: ImportedFile) -> bool:
    """Copies the original into the operation folder if this server can reach both paths.
    True if the file is in place afterwards."""
    op = s.get(Operation, imp.operation_id)
    if op is None:
        return False
    target = operation_paths(s, op)["mcam"]
    try:
        if not os.path.isfile(target):
            if not os.path.isfile(imp.source_path):
                return False
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(imp.source_path, target)
    except OSError:
        return False
    imp.status, imp.copied_at, imp.copied_by = "copied", utcnow(), "server"
    return True


def import_source(s: Session, op: Operation) -> str | None:
    """For the helper: original still to be copied into the operation folder."""
    imp = s.scalar(select(ImportedFile).where(ImportedFile.operation_id == op.id))
    return imp.source_path if imp is not None and imp.status == "pending" else None


@router.post("/admin/import/check")
def check_files(body: PathsIn, s: Session = Depends(get_db), _: User = Depends(require_admin)):
    """Which of these paths were imported before (so the page can mark and skip them)."""
    known = known_files(s, body.paths)
    out = {}
    for p in body.paths:
        imp = known.get(p.strip().lower())
        if imp is not None:
            op = s.get(Operation, imp.operation_id)
            out[p] = {"operation_id": op.id, "article_id": op.article_id, "mdm_number": op.mdm_number}
    return {"known": out}


@router.post("/admin/import/files", status_code=201)
def import_files(body: ImportFilesIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    known = known_files(s, [o.source_path for c in body.cards for o in c.operations])
    op_keys = {fd.key for fd in field_defs(s, "operation") if fd.enabled}
    machines: dict[int, Machine] = {}
    created: list[ImportedFile] = []
    cards_out = []
    for card in body.cards:
        if card.article_id is not None:
            article = s.get(Article, card.article_id)
            if article is None:
                raise HTTPException(422, f"Karte {card.article_id} existiert nicht")
            article_created = False
        else:
            article = Article(data=merge_data(s, "article", {}, card.article), created_by=admin.id)
            s.add(article)
            s.flush()
            article_created = True
        highest = s.scalar(select(func.max(Operation.op_number)).where(Operation.article_id == article.id)) or 0
        ops_out = []
        for item in card.operations:
            path = item.source_path.strip()
            if path.lower() in known:
                ops_out.append({"source_path": path, "skipped": True, "operation_id": known[path.lower()].operation_id})
                continue
            m = machines.get(item.machine_id) or s.get(Machine, item.machine_id)
            if m is None or not m.active:
                raise HTTPException(422, f"Maschine {item.machine_id} nicht gefunden")
            machines[m.id] = m
            highest = (highest // 10 + 1) * 10
            title = item.title.strip() or ntpath.splitext(ntpath.basename(path))[0]
            data = {"prozessbeschreibung": title} if "prozessbeschreibung" in op_keys else {}
            op = Operation(article_id=article.id, mdm_number=next_mdm_number(s), op_number=highest,
                           machine_id=m.id, data=merge_data(s, "operation", {}, data), created_by=admin.id)
            op.machine = m
            if m.nc_naming == "nc":
                op.nc_number = next_nc_number(s, m)
            s.add(op)
            s.flush()
            imp = ImportedFile(operation_id=op.id, source_path=path, size=item.size, modified=item.modified,
                               imported_by=admin.id)
            s.add(imp)
            known[path.lower()] = imp
            created.append(imp)
            ops_out.append({"source_path": path, "operation_id": op.id, "mdm_number": op.mdm_number})
        cards_out.append({"article_id": article.id, "article_created": article_created, "operations": ops_out})
    s.flush()
    audit(s, admin, "file_import", detail=f"{len(created)} Dateien, {len(body.cards)} Karten")
    s.commit()

    if body.copy_now:
        for imp in created:
            server_copy(s, imp)
        s.commit()
    copied = {imp.operation_id for imp in created if imp.status == "copied"}
    for c in cards_out:
        for o in c["operations"]:
            if not o.get("skipped"):
                o["copied"] = o["operation_id"] in copied
    skipped = sum(1 for c in cards_out for o in c["operations"] if o.get("skipped"))
    return {"cards": cards_out, "imported": len(created), "copied": len(copied),
            "pending": len(created) - len(copied), "skipped": skipped}


@router.get("/admin/import/pending")
def pending_files(s: Session = Depends(get_db), _: User = Depends(require_admin)):
    """Imported files not yet copied into their operation folder."""
    rows = list(s.scalars(select(ImportedFile).where(ImportedFile.status == "pending").order_by(ImportedFile.id)))
    items = []
    for imp in rows[:300]:
        op = s.get(Operation, imp.operation_id)
        if op is not None:
            items.append({"operation_id": op.id, "article_id": op.article_id, "mdm_number": op.mdm_number,
                          "source_path": imp.source_path, "target": operation_paths(s, op)["mcam"]})
    return {"count": len(rows), "items": items}


@router.post("/admin/import/copy")
def copy_pending(s: Session = Depends(get_db), _: User = Depends(require_admin)):
    """Try again to copy all pending files on the server (e.g. once the share is reachable)."""
    rows = list(s.scalars(select(ImportedFile).where(ImportedFile.status == "pending")))
    copied = sum(server_copy(s, imp) for imp in rows)
    s.commit()
    return {"copied": copied, "pending": len(rows) - copied}


@router.post("/operations/{operation_id}/import-copied")
def import_copied(operation_id: int, body: CopiedIn, s: Session = Depends(get_db), _: User = Depends(current_user)):
    """The PDB Helper copied the original into the operation folder."""
    imp = s.scalar(select(ImportedFile).where(ImportedFile.operation_id == operation_id))
    if imp is None:
        raise HTTPException(404, "Keine importierte Datei")
    if imp.status != "copied":
        imp.status, imp.copied_at, imp.copied_by = "copied", utcnow(), body.pc_name or "helper"
        s.commit()
    return {"ok": True}
