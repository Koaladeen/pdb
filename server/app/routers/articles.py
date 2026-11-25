"""Part cards (articles), their operations, search, machine change, NC numbers and NC export."""
from datetime import datetime
from typing import Any

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field
from sqlalchemy import func, or_, select
from sqlalchemy.orm import Session, selectinload

from .. import pictures
from ..deps import current_user, get_db, require_admin, stale_before
from ..models import Article, Customer, Lock, Machine, Operation, Revision, User
from ..services import (
    article_out,
    audit,
    field_defs,
    fmt_mdm,
    get_setting,
    header_values,
    lock_out,
    merge_data,
    next_mdm_number,
    next_nc_number,
    operation_out,
    operation_paths,
    require_machine_right,
    status_blocks_export,
)

from .file_import import import_source

router = APIRouter(tags=["parts"])


# ---------------------------------------------------------------- helpers


def get_article(s: Session, article_id: int) -> Article:
    a = s.get(Article, article_id)
    if a is None:
        raise HTTPException(404, "Part not found")
    return a


def get_operation(s: Session, operation_id: int) -> Operation:
    op = s.get(Operation, operation_id)
    if op is None:
        raise HTTPException(404, "Operation not found")
    return op


def active_machine(s: Session, machine_id: int) -> Machine:
    m = s.get(Machine, machine_id)
    if m is None or not m.active:
        raise HTTPException(422, "Machine not found or inactive")
    return m


def ops_with_locks(s: Session, ops: list[Operation], cutoff: datetime) -> list[dict]:
    ids = [op.id for op in ops]
    locks = {l.operation_id: l for l in s.scalars(select(Lock).where(Lock.operation_id.in_(ids)))} if ids else {}
    return [operation_out(op, lock_out(locks.get(op.id), cutoff)) for op in ops]


def operation_detail(s: Session, op: Operation, cutoff: datetime) -> dict:
    lock = s.get(Lock, op.id)
    return {
        **operation_out(op, lock_out(lock, cutoff)),
        "article": article_out(op.article),
        "paths": operation_paths(s, op),
        # the helper puts this .mcam-mmd into the file when it opens it or changes machine
        "machine_definition": op.machine.machine_definition,
        # imported, not copied yet: the helper copies this original into the folder on first open
        "import_source": import_source(s, op),
        # the helper looks for #<prefix>... in posted NC files
        "placeholder_prefix": get_setting(s, "placeholder_prefix", "PDB_"),
    }


# ---------------------------------------------------------------- search and part cards


def _text_cond(model, fd, term: str):
    """Free-text match for one field. Reference fields store ids, so they match by name."""
    col = model.data[fd.key]
    if fd.type == "customer":
        return col.as_integer().in_(select(Customer.id).where(Customer.name.ilike(term)))
    if fd.type == "user":
        return col.as_integer().in_(select(User.id).where(User.display_name.ilike(term)))
    return col.as_string().ilike(term)


@router.get("/articles")
def search_articles(
    request: Request,
    q: str = "",
    machine_id: int | None = None,
    mdm_number: int | None = None,
    nc_number: int | None = None,
    limit: int = 50,
    offset: int = 0,
    s: Session = Depends(get_db),
    _: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    """Free text `q` searches all searchable fields of parts and operations, plus PDB and NC
    numbers. Single fields: ?article.kunde=Muster&operation.prozessbeschreibung=OP (contains)."""
    query = select(Article)
    op_conds = []
    if q.strip():
        term = f"%{q.strip()}%"
        a_or = [_text_cond(Article, fd, term) for fd in field_defs(s, "article") if fd.enabled and fd.searchable]
        o_or = [_text_cond(Operation, fd, term) for fd in field_defs(s, "operation") if fd.enabled and fd.searchable]
        if q.strip().isdigit():
            n = int(q.strip())
            o_or += [Operation.mdm_number == n, Operation.nc_number == n]
        conds = a_or[:]
        if o_or:
            conds.append(Article.id.in_(select(Operation.article_id).where(or_(*o_or))))
        if conds:
            query = query.where(or_(*conds))
    if machine_id is not None:
        op_conds.append(Operation.machine_id == machine_id)
    if mdm_number is not None:
        op_conds.append(Operation.mdm_number == mdm_number)
    if nc_number is not None:
        op_conds.append(Operation.nc_number == nc_number)
    allowed = {
        "article": {fd.key: fd for fd in field_defs(s, "article") if fd.enabled},
        "operation": {fd.key: fd for fd in field_defs(s, "operation") if fd.enabled},
    }
    for name, value in request.query_params.items():
        if "." not in name or value == "":
            continue
        entity, key = name.split(".", 1)
        fd = allowed.get(entity, {}).get(key)
        if fd is None:
            raise HTTPException(422, f"Unknown search field '{name}'")
        col = (Article if entity == "article" else Operation).data[key]
        if fd.type in ("user", "integer", "customer"):
            try:
                cond = col.as_integer() == int(value)
            except ValueError:
                raise HTTPException(422, f"'{name}' needs a number") from None
        elif fd.type == "choice":
            cond = col.as_string() == value
        else:
            cond = col.as_string().ilike(f"%{value}%")
        if entity == "article":
            query = query.where(cond)
        else:
            op_conds.append(cond)
    if op_conds:
        query = query.where(Article.id.in_(select(Operation.article_id).where(*op_conds)))

    total = s.scalar(select(func.count()).select_from(query.subquery()))
    limit = max(1, min(limit, 500))
    # one query for the page's parts, one for all their operations (+ machines), one for locks -
    # instead of one per part (parts can have 100+ operations)
    rows = list(s.scalars(
        query.order_by(Article.id.desc()).limit(limit).offset(max(0, offset))
        .options(selectinload(Article.operations).selectinload(Operation.machine))
    ))
    op_ids = [o.id for a in rows for o in a.operations]
    locks = {l.operation_id: l for l in s.scalars(select(Lock).where(Lock.operation_id.in_(op_ids)))} if op_ids else {}
    return {
        "total": total,
        "items": [
            article_out(a, [list_operation(o, lock_out(locks.get(o.id), cutoff)) for o in a.operations])
            for a in rows
        ],
    }


def list_operation(op: Operation, lock: dict | None) -> dict:
    """Light operation for result lists: what the list and the thumbnail need, not all fields.
    The preview loads the full card."""
    return {
        "id": op.id,
        "mdm_number": op.mdm_number,
        "mdm_number_text": fmt_mdm(op.mdm_number),
        "op_number": op.op_number,
        "machine": op.machine.name,
        "picture_at": pictures.updated_at(op.id),
        "lock": lock,
    }


class ArticleIn(BaseModel):
    data: dict[str, Any] = {}


@router.post("/articles", status_code=201)
def create_article(body: ArticleIn, s: Session = Depends(get_db), user: User = Depends(current_user)):
    a = Article(data=merge_data(s, "article", {}, body.data), created_by=user.id)
    s.add(a)
    s.flush()
    audit(s, user, "article_create", article_id=a.id)
    s.commit()
    return article_out(a, [])


@router.get("/articles/{article_id}")
def read_article(
    article_id: int,
    s: Session = Depends(get_db),
    _: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    a = get_article(s, article_id)
    return article_out(a, ops_with_locks(s, list(a.operations), cutoff))


@router.patch("/articles/{article_id}")
def update_article(
    article_id: int,
    body: ArticleIn,
    s: Session = Depends(get_db),
    user: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    a = get_article(s, article_id)
    a.data = merge_data(s, "article", a.data, body.data)
    audit(s, user, "article_update", article_id=a.id, detail=", ".join(sorted(body.data)))
    s.commit()
    return article_out(a, ops_with_locks(s, list(a.operations), cutoff))


@router.delete("/articles/{article_id}")
def delete_article(article_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    a = get_article(s, article_id)
    if a.operations:
        raise HTTPException(409, "Delete the operations first")
    s.delete(a)
    audit(s, admin, "article_delete", article_id=article_id)
    s.commit()
    return {"result": "deleted"}


# ---------------------------------------------------------------- operations


class OperationIn(BaseModel):
    machine_id: int
    op_number: int | None = Field(default=None, ge=0)
    data: dict[str, Any] = {}
    assign_nc_number: bool = False


class OperationPatch(BaseModel):
    op_number: int | None = Field(default=None, ge=0)
    data: dict[str, Any] = {}


@router.post("/articles/{article_id}/operations", status_code=201)
def create_operation(
    article_id: int,
    body: OperationIn,
    s: Session = Depends(get_db),
    user: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    """Creates an operation with the next PDB number. Machines that name programs by NC number
    (FANUC) get the next NC number straight away."""
    a = get_article(s, article_id)
    m = active_machine(s, body.machine_id)
    require_machine_right(s, user, m.id)
    data = merge_data(s, "operation", {}, body.data)
    op_number = body.op_number
    if op_number is None:
        highest = max((o.op_number for o in a.operations), default=0)
        op_number = (highest // 10 + 1) * 10
    op = Operation(
        article_id=a.id,
        mdm_number=next_mdm_number(s),
        op_number=op_number,
        machine_id=m.id,
        data=data,
        created_by=user.id,
    )
    op.machine = m
    if body.assign_nc_number or m.nc_naming == "nc":
        op.nc_number = next_nc_number(s, m)
    s.add(op)
    s.flush()
    audit(s, user, "operation_create", article_id=a.id, operation_id=op.id, detail=f"MDM{op.mdm_number} on {m.name}")
    s.commit()
    return operation_detail(s, op, cutoff)


@router.get("/operations/{operation_id}")
def read_operation(
    operation_id: int,
    s: Session = Depends(get_db),
    _: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    return operation_detail(s, get_operation(s, operation_id), cutoff)


@router.get("/operations/by-mdm/{mdm_number}")
def read_operation_by_mdm(
    mdm_number: int,
    s: Session = Depends(get_db),
    _: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    op = s.scalar(select(Operation).where(Operation.mdm_number == mdm_number))
    if op is None:
        raise HTTPException(404, "Operation not found")
    return operation_detail(s, op, cutoff)


@router.patch("/operations/{operation_id}")
def update_operation(
    operation_id: int,
    body: OperationPatch,
    s: Session = Depends(get_db),
    user: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    if body.op_number is not None:
        op.op_number = body.op_number
    if body.data:
        op.data = merge_data(s, "operation", op.data, body.data)
    audit(s, user, "operation_update", article_id=op.article_id, operation_id=op.id, detail=", ".join(sorted(body.data)))
    s.commit()
    return operation_detail(s, op, cutoff)


@router.delete("/operations/{operation_id}")
def delete_operation(operation_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    """Removes the record only; files on the share stay where they are."""
    op = get_operation(s, operation_id)
    if s.get(Lock, op.id) is not None:
        raise HTTPException(409, "Operation is checked out")
    s.query(Revision).filter(Revision.operation_id == op.id).delete()
    detail = f"MDM{op.mdm_number}"
    article_id = op.article_id
    pictures.delete(op.id)
    s.delete(op)
    audit(s, admin, "operation_delete", article_id=article_id, operation_id=operation_id, detail=detail)
    s.commit()
    return {"result": "deleted"}


class MachineChangeIn(BaseModel):
    machine_id: int
    pc_name: str = ""


@router.post("/operations/{operation_id}/machine")
def change_machine(
    operation_id: int,
    body: MachineChangeIn,
    s: Session = Depends(get_db),
    user: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    """Moves the operation to another machine. The NC number belongs to the old machine's
    sequence, so it is dropped (and a new one assigned for FANUC-style machines). The response
    has old and new paths: the add-in moves the operation folder and switches the machine
    definition in Mastercam."""
    op = get_operation(s, operation_id)
    new = active_machine(s, body.machine_id)
    require_machine_right(s, user, op.machine_id)
    require_machine_right(s, user, new.id)
    lock = s.get(Lock, op.id)
    if lock is not None and lock.last_heartbeat >= cutoff and lock.user_id != user.id:
        raise HTTPException(409, {"message": "Operation is checked out by someone else", "lock": lock_out(lock, cutoff)})
    if new.id == op.machine_id:
        raise HTTPException(422, "Operation is already on this machine")
    old_paths = operation_paths(s, op)
    old_name = op.machine.name
    op.machine_id = new.id
    op.machine = new
    op.nc_number = None
    s.flush()
    if new.nc_naming == "nc":
        op.nc_number = next_nc_number(s, new)
    audit(s, user, "machine_change", article_id=op.article_id, operation_id=op.id, detail=f"{old_name} -> {new.name}")
    s.commit()
    return {**operation_detail(s, op, cutoff), "old_paths": old_paths}


@router.post("/operations/{operation_id}/nc-number")
def assign_nc_number(
    operation_id: int,
    s: Session = Depends(get_db),
    user: User = Depends(current_user),
    cutoff: datetime = Depends(stale_before),
):
    """The "next free number" button. Keeps an existing number."""
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    if op.nc_number is None:
        op.nc_number = next_nc_number(s, op.machine)
        audit(s, user, "nc_number", article_id=op.article_id, operation_id=op.id, detail=str(op.nc_number))
        s.commit()
    return operation_detail(s, op, cutoff)


def _info_file(s: Session, op: Operation, user: User, paths: dict) -> str:
    a = op.article.data
    return "\r\n".join(
        [
            f"{'MDM' if paths['nc_info'].upper().endswith('.MDM') else 'PDB'}-Information",
            f"Transfer by : {user.display_name}",
            f"Description : {a.get('zeichnungsnummer', '')} ♦ {a.get('teilename', '')}",
            f"Drawing No  : {a.get('zeichnungsnummer', '')}",
            f"NC-Operation: {op.data.get('prozessbeschreibung', '')}",
            f"DNC-Location: {paths['nc_export']}",
            f"NET-Location: {paths['nc_posted']}",
            f"Copy Date   : {datetime.now().strftime('%d.%m.%Y %H:%M:%S')}",
            "================================================",
            "",
        ]
    )


@router.post("/operations/{operation_id}/nc-export")
def nc_export(operation_id: int, s: Session = Depends(get_db), user: User = Depends(current_user)):
    """Checks that the export is allowed and returns everything the add-in needs: where to post,
    where to copy, the header placeholder values and the text of the optional info file."""
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    blocked = status_blocks_export(s, op)
    if blocked:
        raise HTTPException(409, f"Program is locked for production (status '{blocked}')")
    paths = operation_paths(s, op)
    if "nc_export" not in paths:
        raise HTTPException(409, "This machine names programs by NC number; assign one first")
    audit(s, user, "nc_export", article_id=op.article_id, operation_id=op.id, detail=paths["nc_export"])
    s.commit()
    return {
        "paths": paths,
        "machine": {
            "name": op.machine.name,
            "machine_definition": op.machine.machine_definition,
            "nc_extension": op.machine.nc_extension,
        },
        "program_name": paths["nc_export"].rsplit("\\", 1)[-1].rsplit(".", 1)[0],
        "header_values": header_values(s, op),
        "info_file_text": _info_file(s, op, user, paths) if "nc_info" in paths else "",
        # the helper fills every #<prefix>... in the posted header (#<prefix>PGM = program name)
        "placeholder_prefix": get_setting(s, "placeholder_prefix", "PDB_"),
    }


@router.post("/operations/{operation_id}/toollist")
def tool_list(operation_id: int, s: Session = Depends(get_db), user: User = Depends(current_user)):
    """Authorises and logs tool list creation; returns template, target and field values."""
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    paths = operation_paths(s, op)
    template = paths["folder"].rsplit("\\", 1)[0] + "\\Template-ToolList.docx"
    audit(s, user, "toollist", article_id=op.article_id, operation_id=op.id)
    s.commit()
    return {"template": template, "target": paths["tool_list"], "header_values": header_values(s, op)}
