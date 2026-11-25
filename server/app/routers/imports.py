"""Reading existing numbered operation folders back into the database (admin only).
The scanning happens on a PC that sees the share (scripts/import_folders.py); this endpoint
only records one operation with its existing number. Safe to repeat: existing numbers
are left alone."""
from typing import Any

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from ..deps import get_db, require_admin
from ..models import Article, Machine, Operation, User
from ..services import audit, merge_data

router = APIRouter(prefix="/admin/import", tags=["admin"])


class ImportOperation(BaseModel):
    machine_id: int
    mdm_number: int = Field(ge=1)
    op_number: int | None = Field(default=None, ge=0)
    # Part: found by drawing number + part name (case-insensitive), otherwise created.
    article: dict[str, Any]
    operation: dict[str, Any] = {}
    dry_run: bool = False


def _find_article(s: Session, data: dict) -> Article | None:
    zn = (data.get("zeichnungsnummer") or "").strip().lower()
    tn = (data.get("teilename") or "").strip().lower()
    if not tn:
        return None
    q = select(Article).where(func.lower(Article.data["teilename"].as_string()) == tn)
    if zn:
        q = q.where(func.lower(Article.data["zeichnungsnummer"].as_string()) == zn)
    return s.scalars(q.order_by(Article.id)).first()


@router.post("/operation")
def import_operation(body: ImportOperation, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    existing = s.scalar(select(Operation).where(Operation.mdm_number == body.mdm_number))
    if existing is not None:
        return {"result": "exists", "operation_id": existing.id, "article_id": existing.article_id}
    machine = s.get(Machine, body.machine_id)
    if machine is None:
        raise HTTPException(422, "Machine not found")

    article = _find_article(s, body.article)
    article_created = article is None
    if article is None:
        article = Article(data=merge_data(s, "article", {}, body.article), created_by=admin.id)
        s.add(article)
        s.flush()
    op_number = body.op_number
    if op_number is None:
        highest = s.scalar(select(func.max(Operation.op_number)).where(Operation.article_id == article.id)) or 0
        op_number = (highest // 10 + 1) * 10
    op = Operation(
        article_id=article.id,
        mdm_number=body.mdm_number,
        op_number=op_number,
        machine_id=machine.id,
        data=merge_data(s, "operation", {}, body.operation),
        created_by=admin.id,
    )
    s.add(op)
    s.flush()
    result = {
        "result": "would create" if body.dry_run else "created",
        "operation_id": op.id,
        "article_id": article.id,
        "article_created": article_created,
        "op_number": op_number,
    }
    if body.dry_run:
        s.rollback()
        result.pop("operation_id")
        if article_created:
            result.pop("article_id")
        return result
    audit(s, admin, "operation_import", article_id=article.id, operation_id=op.id, detail=f"MDM{body.mdm_number} from folder")
    s.commit()
    return result
