"""Read-only lists every logged-in user needs: machines, fields, users, suggestions."""
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy import select
from sqlalchemy.orm import Session

from ..deps import current_user, get_db
from ..models import ENTITIES, Article, ChoiceValue, FieldDefinition, Machine, Operation, User
from ..services import field_defs, has_machine_right, machine_out
from .admin import field_out

router = APIRouter(tags=["catalog"])


@router.get("/machines")
def machines(s: Session = Depends(get_db), user: User = Depends(current_user)):
    return [
        {**machine_out(m), "allowed": has_machine_right(s, user, m.id)}
        for m in s.scalars(select(Machine).where(Machine.active).order_by(Machine.name))
    ]


@router.get("/fields")
def fields(entity: str | None = None, s: Session = Depends(get_db), _: User = Depends(current_user)):
    """Enabled fields in display order: what forms, lists and search should show."""
    out = []
    for ent in [entity] if entity else ENTITIES:
        if ent not in ENTITIES:
            raise HTTPException(422, f"entity must be one of {ENTITIES}")
        out += [field_out(fd) for fd in field_defs(s, ent, include_system=True) if fd.enabled]
    return out


@router.get("/users")
def users(s: Session = Depends(get_db), _: User = Depends(current_user)):
    """For user pickers (CAM programmer, CNC operator). Inactive users are included so old
    entries still show a name; pickers offer only active ones."""
    return [
        {"id": u.id, "display_name": u.display_name, "active": u.active}
        for u in s.scalars(select(User).order_by(User.display_name))
    ]


@router.get("/suggest/{entity}/{key}")
def suggest(
    entity: str,
    key: str,
    q: str = "",
    limit: int = 20,
    s: Session = Depends(get_db),
    _: User = Depends(current_user),
):
    """Dropdown values for a field: admin-defined values first, then values used before."""
    fd = s.scalar(
        select(FieldDefinition).where(
            FieldDefinition.entity == entity, FieldDefinition.key == key, FieldDefinition.active
        )
    )
    if fd is None or fd.system:
        raise HTTPException(404, "Field not found")
    limit = max(1, min(limit, 100))
    values: list[str] = []
    for v in s.scalars(
        select(ChoiceValue.value)
        .where(ChoiceValue.field_id == fd.id, ChoiceValue.active)
        .order_by(ChoiceValue.sort_order, ChoiceValue.value)
    ):
        if q.lower() in v.lower():
            values.append(v)
    if fd.type == "suggest":
        model = Article if entity == "article" else Operation
        col = model.data[key].as_string()
        used = s.scalars(
            select(col).where(col.is_not(None), col.ilike(f"%{q}%")).distinct().order_by(col).limit(limit)
        )
        values += [v for v in used if v and v not in values]
    return values[:limit]
