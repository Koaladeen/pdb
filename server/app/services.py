"""Shared logic: number counters, field validation, share paths, NC header values, audit."""
import ntpath
import re
from collections.abc import Callable
from datetime import date
from typing import Any

from fastapi import HTTPException
from sqlalchemy import select, update
from sqlalchemy.orm import Session

from . import pictures
from .models import (
    Article,
    AuditEntry,
    ChoiceValue,
    Counter,
    Customer,
    FieldDefinition,
    Lock,
    Machine,
    MachineRight,
    Operation,
    Setting,
    User,
)

# ---------------------------------------------------------------- settings, audit, rights


def get_setting(s: Session, key: str, default: str = "") -> str:
    row = s.get(Setting, key)
    return row.value if row else default


def audit(
    s: Session,
    user: User | None,
    action: str,
    *,
    article_id: int | None = None,
    operation_id: int | None = None,
    detail: str = "",
) -> None:
    s.add(
        AuditEntry(
            user_id=user.id if user else None,
            article_id=article_id,
            operation_id=operation_id,
            action=action,
            detail=detail,
        )
    )


def has_machine_right(s: Session, user: User, machine_id: int) -> bool:
    if user.is_admin:
        return True
    return s.get(MachineRight, (user.id, machine_id)) is not None


def require_machine_right(s: Session, user: User, machine_id: int) -> None:
    if not has_machine_right(s, user, machine_id):
        raise HTTPException(403, "No right for this machine")


# ---------------------------------------------------------------- counters


def nc_counter_name(machine_id: int) -> str:
    return f"nc:{machine_id}"


def next_number(
    s: Session, name: str, taken: Callable[[int], bool], maximum: int | None = None
) -> int:
    """Hand out the next free number of a counter. The UPDATE takes a write lock (SQLite) or row
    lock (Postgres) until commit, so two users can never receive the same number. Numbers that
    are already used (e.g. after an admin lowered the counter) are skipped."""
    if s.get(Counter, name) is None:
        s.add(Counter(name=name, value=0))
        s.flush()
    for _ in range(100_000):
        s.execute(update(Counter).where(Counter.name == name).values(value=Counter.value + 1))
        n = s.scalar(select(Counter.value).where(Counter.name == name))
        if maximum is not None and n > maximum:
            raise HTTPException(409, f"Number range for {name} is used up (max {maximum})")
        if not taken(n):
            return n
    raise HTTPException(409, f"No free number found for {name}")


def next_mdm_number(s: Session) -> int:
    return next_number(
        s,
        "mdm",
        lambda n: s.scalar(select(Operation.id).where(Operation.mdm_number == n)) is not None,
    )


def next_nc_number(s: Session, machine: Machine) -> int:
    return next_number(
        s,
        nc_counter_name(machine.id),
        lambda n: s.scalar(
            select(Operation.id).where(Operation.machine_id == machine.id, Operation.nc_number == n)
        )
        is not None,
        machine.nc_number_max,
    )


# ---------------------------------------------------------------- configurable fields

_DURATION = re.compile(r"^\d{1,4}:[0-5]\d:[0-5]\d$")


def field_defs(s: Session, entity: str, *, include_system: bool = False) -> list[FieldDefinition]:
    q = select(FieldDefinition).where(FieldDefinition.entity == entity, FieldDefinition.active)
    if not include_system:
        q = q.where(FieldDefinition.system.is_(False))
    return list(s.scalars(q.order_by(FieldDefinition.sort_order, FieldDefinition.id)))


def _check_value(s: Session, fd: FieldDefinition, value: Any) -> Any:
    def bad(msg: str) -> HTTPException:
        return HTTPException(422, f"Field '{fd.key}': {msg}")

    t = fd.type
    if t in ("text", "longtext", "suggest", "choice"):
        if not isinstance(value, str):
            raise bad("must be text")
        value = value.strip() if t != "longtext" else value
        if t == "choice" and value:
            ok = s.scalar(
                select(ChoiceValue.id).where(
                    ChoiceValue.field_id == fd.id, ChoiceValue.value == value, ChoiceValue.active
                )
            )
            if ok is None:
                raise bad(f"'{value}' is not an allowed value")
        return value
    if t == "integer":
        if isinstance(value, bool) or not isinstance(value, int):
            raise bad("must be a whole number")
        return value
    if t == "number":
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise bad("must be a number")
        return value
    if t == "boolean":
        if not isinstance(value, bool):
            raise bad("must be true or false")
        return value
    if t == "date":
        try:
            date.fromisoformat(value)
        except (TypeError, ValueError):
            raise bad("must be a date YYYY-MM-DD") from None
        return value
    if t == "duration":
        if not isinstance(value, str) or not _DURATION.match(value):
            raise bad("must be a duration HH:MM:SS")
        return value
    if t == "user":
        if isinstance(value, bool) or not isinstance(value, int) or s.get(User, value) is None:
            raise bad("must be an existing user id")
        return value
    if t == "customer":
        c = s.get(Customer, value) if isinstance(value, int) and not isinstance(value, bool) else None
        if c is None or not c.active:
            raise bad("must be an existing, active customer id")
        return value
    raise bad(f"unknown field type {t}")


def _empty(v: Any) -> bool:
    return v is None or v == ""


def merge_data(s: Session, entity: str, current: dict, incoming: dict) -> dict:
    """Validate `incoming` against the enabled fields and merge it into `current`.
    A value of null removes the field. Values of disabled fields are kept untouched."""
    defs = {fd.key: fd for fd in field_defs(s, entity)}
    result = dict(current or {})
    for key, value in incoming.items():
        fd = defs.get(key)
        if fd is None:
            raise HTTPException(422, f"Unknown field '{key}'")
        if not fd.enabled:
            raise HTTPException(422, f"Field '{key}' is disabled")
        if _empty(value):
            result.pop(key, None)
        else:
            result[key] = _check_value(s, fd, value)
    missing = [fd.label for fd in defs.values() if fd.enabled and fd.required and _empty(result.get(fd.key))]
    if missing:
        raise HTTPException(422, f"Required fields missing: {', '.join(missing)}")
    return result


def status_blocks_export(s: Session, op: Operation) -> str | None:
    """Returns the blocking status value if the operation or its part is locked for production."""
    checks = [("operation", op.data.get("operations_status")), ("article", op.article.data.get("status"))]
    for entity, value in checks:
        if not value:
            continue
        blocked = s.scalar(
            select(ChoiceValue.value)
            .join(FieldDefinition)
            .where(
                FieldDefinition.entity == entity,
                FieldDefinition.type == "choice",
                FieldDefinition.key.in_(["status", "operations_status"]),
                ChoiceValue.value == value,
                ChoiceValue.blocks_export,
            )
        )
        if blocked:
            return blocked
    return None


# ---------------------------------------------------------------- share paths
# Paths are Windows paths as the PCs see them (mapped drives). The server never touches the
# share; it only computes and hands out paths, the add-in reads and writes the files.


def fmt_mdm(n: int) -> str:
    return f"{n:08d}"


def operation_paths(s: Session, op: Operation, next_revision: int | None = None) -> dict[str, str]:
    mcam_root = get_setting(s, "mcam_root")
    nc_root = get_setting(s, "nc_root")
    archive_sub = get_setting(s, "archive_subfolder", "Revisionen")
    num = fmt_mdm(op.mdm_number)
    folder = ntpath.join(mcam_root, op.machine.mcam_folder, f"MDM{op.mdm_number}")
    rev = next_revision if next_revision is not None else op.current_revision + 1
    paths = {
        "folder": folder,
        "mcam": ntpath.join(folder, f"{num}.mcam"),
        "nc_posted": ntpath.join(folder, f"{num}.nc"),
        "nc_old": ntpath.join(folder, f"{num}-old.nc"),
        "tool_list": ntpath.join(folder, f"{num}-ToolList.docx"),
        "picture": ntpath.join(folder, f"Pic{num}.png"),
        "next_archive": ntpath.join(folder, archive_sub, f"{num}_r{rev:04d}.mcam"),
    }
    target = nc_target_name(op)
    if target:
        paths["nc_export"] = ntpath.join(nc_root, op.machine.nc_folder, target)
        paths["nc_info"] = ntpath.join(nc_root, op.machine.nc_folder, ntpath.splitext(target)[0] + ".MDM")
    return paths


def nc_target_name(op: Operation) -> str | None:
    """Heidenhain style: 00084197.H; FANUC style (machine naming "nc"): 1101.NC.
    None if the machine needs an NC program number and none is assigned yet."""
    m = op.machine
    if m.nc_naming == "nc":
        if op.nc_number is None:
            return None
        return f"{op.nc_number}{m.nc_extension}"
    return f"{fmt_mdm(op.mdm_number)}{m.nc_extension}"


def header_values(s: Session, op: Operation) -> dict[str, str]:
    """Values for the NC header placeholders, keyed by the full placeholder ("#MDM_FN")."""
    values: dict[str, str] = {}
    for entity, source in (("article", op.article.data), ("operation", op.data)):
        for fd in field_defs(s, entity, include_system=True):
            if not fd.placeholder or not fd.enabled:
                continue
            if fd.system:
                raw = getattr(op, fd.key, None)
                if fd.key == "mdm_number":
                    raw = fmt_mdm(op.mdm_number)
                elif fd.key == "machine_id":
                    raw = op.machine.name
            else:
                raw = source.get(fd.key)
                if fd.type == "user" and raw is not None:
                    u = s.get(User, raw)
                    raw = u.display_name if u else ""
                elif fd.type == "customer" and raw is not None:
                    c = s.get(Customer, raw)
                    raw = c.name if c else ""
            values[f"#{fd.placeholder}"] = "" if raw is None else str(raw)
    return values


# ---------------------------------------------------------------- output shapes


def user_out(u: User) -> dict:
    return {
        "id": u.id,
        "username": u.username,
        "display_name": u.display_name,
        "role": u.role,
        "active": u.active,
        "has_password": u.has_password,
        "avatar_at": pictures.avatars.updated_at(u.id),
    }


def machine_out(m: Machine) -> dict:
    return {
        "id": m.id,
        "name": m.name,
        "mcam_folder": m.mcam_folder,
        "nc_folder": m.nc_folder,
        "nc_naming": m.nc_naming,
        "nc_extension": m.nc_extension,
        "nc_number_max": m.nc_number_max,
        "machine_definition": m.machine_definition,
        "post": m.post,
        "header_template": m.header_template,
        "active": m.active,
    }


def lock_out(lock: Lock | None, stale_before) -> dict | None:
    if lock is None:
        return None
    return {
        "user_id": lock.user_id,
        "user": lock.user.display_name,
        "pc_name": lock.pc_name,
        "locked_at": lock.locked_at.isoformat(),
        "last_heartbeat": lock.last_heartbeat.isoformat(),
        "stale": lock.last_heartbeat < stale_before,
    }


def operation_out(op: Operation, lock: dict | None = None) -> dict:
    return {
        "id": op.id,
        "article_id": op.article_id,
        "mdm_number": op.mdm_number,
        "mdm_number_text": fmt_mdm(op.mdm_number),
        "op_number": op.op_number,
        "nc_number": op.nc_number,
        "machine_id": op.machine_id,
        "machine": op.machine.name,
        "current_revision": op.current_revision,
        "picture_at": pictures.updated_at(op.id),
        "data": op.data,
        "lock": lock,
        "created_at": op.created_at.isoformat(),
        "updated_at": op.updated_at.isoformat(),
    }


def article_out(a: Article, ops: list[dict] | None = None) -> dict:
    out = {
        "id": a.id,
        "data": a.data,
        "created_at": a.created_at.isoformat(),
        "updated_at": a.updated_at.isoformat(),
    }
    if ops is not None:
        out["operations"] = ops
    return out
