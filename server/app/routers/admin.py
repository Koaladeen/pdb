"""Admin panel API: users and rights, machines, fields and choice values, settings, counters, audit."""
import re

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field
from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session

from .. import pictures
from ..deps import get_db, require_admin
from .auth import read_avatar
from ..models import (
    ENTITIES,
    FIELD_TYPES,
    NC_NAMING,
    ROLES,
    ApiToken,
    Article,
    AuditEntry,
    Lock,
    Revision,
    ChoiceValue,
    Counter,
    FieldDefinition,
    Machine,
    MachineRight,
    Operation,
    Setting,
    User,
)
from ..security import hash_password
from ..services import audit, machine_out, nc_counter_name, user_out

router = APIRouter(prefix="/admin", tags=["admin"])


def _apply(obj, body: BaseModel) -> None:
    for key in body.model_fields_set:
        setattr(obj, key, getattr(body, key))


# ---------------------------------------------------------------- users and rights


class UserIn(BaseModel):
    username: str = Field(min_length=1, max_length=100)
    display_name: str = Field(min_length=1, max_length=200)
    password: str | None = Field(default=None, min_length=4)  # None = login by picking the name
    role: str = "user"
    machine_ids: list[int] = []


class UserPatch(BaseModel):
    display_name: str | None = None
    password: str | None = Field(default=None, min_length=4)
    remove_password: bool = False
    role: str | None = None
    active: bool | None = None


def _admin_needs_password(role: str, has_password: bool) -> None:
    # Without a password anyone on the network could pick the admin from the login list.
    if role == "admin" and not has_password:
        raise HTTPException(409, "Admins need a password")


class RightsIn(BaseModel):
    machine_ids: list[int]


def _check_role(role: str | None) -> None:
    if role is not None and role not in ROLES:
        raise HTTPException(422, f"Role must be one of {ROLES}")


def _active_admins(s: Session) -> int:
    return s.scalar(select(func.count()).select_from(User).where(User.role == "admin", User.active))


def _set_rights(s: Session, user_id: int, machine_ids: list[int]) -> None:
    for mid in machine_ids:
        if s.get(Machine, mid) is None:
            raise HTTPException(422, f"Machine {mid} does not exist")
    s.execute(delete(MachineRight).where(MachineRight.user_id == user_id))
    for mid in set(machine_ids):
        s.add(MachineRight(user_id=user_id, machine_id=mid))


def _user_with_rights(s: Session, u: User) -> dict:
    ids = list(s.scalars(select(MachineRight.machine_id).where(MachineRight.user_id == u.id)))
    return {**user_out(u), "machine_ids": sorted(ids)}


@router.get("/users")
def list_users(s: Session = Depends(get_db), _=Depends(require_admin)):
    return [_user_with_rights(s, u) for u in s.scalars(select(User).order_by(User.username))]


@router.post("/users", status_code=201)
def create_user(body: UserIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    _check_role(body.role)
    _admin_needs_password(body.role, bool(body.password))
    if s.scalar(select(User).where(User.username == body.username)):
        raise HTTPException(409, "Username already exists")
    u = User(
        username=body.username,
        display_name=body.display_name,
        password_hash=hash_password(body.password) if body.password else "",
        role=body.role,
    )
    s.add(u)
    s.flush()
    _set_rights(s, u.id, body.machine_ids)
    audit(s, admin, "user_create", detail=u.username)
    s.commit()
    return _user_with_rights(s, u)


@router.patch("/users/{user_id}")
def update_user(user_id: int, body: UserPatch, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    u = s.get(User, user_id) or _404("User")
    _check_role(body.role)
    losing_admin = u.role == "admin" and u.active and (
        (body.role is not None and body.role != "admin") or body.active is False
    )
    if losing_admin and _active_admins(s) <= 1:
        raise HTTPException(409, "Cannot remove the last admin")
    final_role = body.role or u.role
    final_has_password = bool(body.password) or (u.has_password and not body.remove_password)
    _admin_needs_password(final_role, final_has_password)
    for key in body.model_fields_set - {"password", "remove_password"}:
        setattr(u, key, getattr(body, key))
    if body.password:
        u.password_hash = hash_password(body.password)
    elif body.remove_password:
        u.password_hash = ""
    if body.active is False or body.password:
        s.execute(delete(ApiToken).where(ApiToken.user_id == u.id))
    audit(s, admin, "user_update", detail=u.username)
    s.commit()
    return _user_with_rights(s, u)


@router.put("/users/{user_id}/avatar")
async def set_user_avatar(user_id: int, request: Request, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    u = s.get(User, user_id) or _404("User")
    pictures.avatars.save(u.id, await read_avatar(request))
    audit(s, admin, "avatar_set", detail=u.username)
    s.commit()
    return _user_with_rights(s, u)


@router.delete("/users/{user_id}/avatar")
def delete_user_avatar(user_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    u = s.get(User, user_id) or _404("User")
    pictures.avatars.delete(u.id)
    audit(s, admin, "avatar_delete", detail=u.username)
    s.commit()
    return _user_with_rights(s, u)


@router.put("/users/{user_id}/machines")
def set_user_machines(user_id: int, body: RightsIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    u = s.get(User, user_id) or _404("User")
    _set_rights(s, u.id, body.machine_ids)
    audit(s, admin, "rights_update", detail=f"{u.username}: {sorted(set(body.machine_ids))}")
    s.commit()
    return _user_with_rights(s, u)


@router.delete("/users/{user_id}")
def delete_user(user_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    """Deletes the user; a user who already appears in history is deactivated instead."""
    u = s.get(User, user_id) or _404("User")
    if u.id == admin.id:
        raise HTTPException(409, "You cannot delete yourself")
    if u.role == "admin" and u.active and _active_admins(s) <= 1:
        raise HTTPException(409, "Cannot remove the last admin")
    name = u.username
    s.execute(delete(ApiToken).where(ApiToken.user_id == u.id))
    s.execute(delete(MachineRight).where(MachineRight.user_id == u.id))
    referenced = any(
        s.scalar(select(col).where(col == u.id).limit(1)) is not None
        for col in (
            Revision.saved_by,
            Lock.user_id,
            AuditEntry.user_id,
            Article.created_by,
            Operation.created_by,
        )
    )
    if referenced:
        u.active = False
        result = "deactivated"
    else:
        s.delete(u)
        result = "deleted"
    audit(s, admin, f"user_{result}", detail=name)
    s.commit()
    return {"result": result}


# ---------------------------------------------------------------- machines


class MachineIn(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    mcam_folder: str = Field(min_length=1, max_length=200)
    nc_folder: str = Field(min_length=1, max_length=200)
    nc_naming: str = "mdm"
    nc_extension: str = ".nc"
    nc_number_max: int | None = None
    machine_definition: str = ""
    post: str = ""
    header_template: str = ""
    active: bool = True


class MachinePatch(BaseModel):
    name: str | None = None
    mcam_folder: str | None = None
    nc_folder: str | None = None
    nc_naming: str | None = None
    nc_extension: str | None = None
    nc_number_max: int | None = None
    machine_definition: str | None = None
    post: str | None = None
    header_template: str | None = None
    active: bool | None = None


_FOLDER_BAD = re.compile(r'[<>:"/\\|?*]')


def _check_machine(body: BaseModel) -> None:
    for key in ("mcam_folder", "nc_folder"):
        v = getattr(body, key, None)
        if v is not None and (_FOLDER_BAD.search(v) or v.strip(" .") != v or not v):
            raise HTTPException(422, f"{key} must be a single folder name without \\ / : * ? \" < > |")
    if getattr(body, "nc_naming", None) is not None and body.nc_naming not in NC_NAMING:
        raise HTTPException(422, f"nc_naming must be one of {NC_NAMING}")
    ext = getattr(body, "nc_extension", None)
    if ext is not None and not re.fullmatch(r"\.[A-Za-z0-9]{1,10}", ext):
        raise HTTPException(422, "nc_extension must look like .H or .NC")


@router.get("/machines")
def list_machines(s: Session = Depends(get_db), _=Depends(require_admin)):
    out = []
    for m in s.scalars(select(Machine).order_by(Machine.name)):
        c = s.get(Counter, nc_counter_name(m.id))
        out.append({**machine_out(m), "nc_counter": c.value if c else 0})
    return out


@router.post("/machines", status_code=201)
def create_machine(body: MachineIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    _check_machine(body)
    if s.scalar(select(Machine).where(Machine.name == body.name)):
        raise HTTPException(409, "Machine name already exists")
    m = Machine(**body.model_dump())
    s.add(m)
    s.flush()
    s.add(Counter(name=nc_counter_name(m.id), value=0))
    audit(s, admin, "machine_create", detail=m.name)
    s.commit()
    return machine_out(m)


@router.patch("/machines/{machine_id}")
def update_machine(machine_id: int, body: MachinePatch, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    m = s.get(Machine, machine_id) or _404("Machine")
    _check_machine(body)
    if body.name and body.name != m.name and s.scalar(select(Machine).where(Machine.name == body.name)):
        raise HTTPException(409, "Machine name already exists")
    _apply(m, body)
    audit(s, admin, "machine_update", detail=m.name)
    s.commit()
    return machine_out(m)


@router.delete("/machines/{machine_id}")
def delete_machine(machine_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    """Deletes an unused machine; a machine with operations is only deactivated."""
    m = s.get(Machine, machine_id) or _404("Machine")
    used = s.scalar(select(Operation.id).where(Operation.machine_id == m.id).limit(1))
    if used:
        m.active = False
        result = "deactivated"
    else:
        s.execute(delete(MachineRight).where(MachineRight.machine_id == m.id))
        s.execute(delete(Counter).where(Counter.name == nc_counter_name(m.id)))
        s.delete(m)
        result = "deleted"
    audit(s, admin, f"machine_{result}", detail=m.name)
    s.commit()
    return {"result": result}


# ---------------------------------------------------------------- fields and choices


class FieldIn(BaseModel):
    entity: str
    key: str = Field(pattern=r"^[a-z][a-z0-9_]{0,99}$")
    label: str = Field(min_length=1, max_length=200)
    type: str = "text"
    enabled: bool = True
    required: bool = False
    searchable: bool = False
    show_in_list: bool = False
    sort_order: int = 1000
    placeholder: str | None = Field(default=None, pattern=r"^[A-Za-z][A-Za-z0-9_]{0,99}$")


class FieldPatch(BaseModel):
    label: str | None = None
    type: str | None = None
    enabled: bool | None = None
    required: bool | None = None
    searchable: bool | None = None
    show_in_list: bool | None = None
    sort_order: int | None = None
    placeholder: str | None = Field(default=None, pattern=r"^[A-Za-z][A-Za-z0-9_]{0,99}$")


class ChoiceIn(BaseModel):
    value: str = Field(min_length=1, max_length=200)
    sort_order: int = 0
    blocks_export: bool = False


class ChoicePatch(BaseModel):
    value: str | None = Field(default=None, min_length=1, max_length=200)
    sort_order: int | None = None
    active: bool | None = None
    blocks_export: bool | None = None


def field_out(fd: FieldDefinition, with_inactive_choices: bool = False) -> dict:
    return {
        "id": fd.id,
        "entity": fd.entity,
        "key": fd.key,
        "label": fd.label,
        "type": fd.type,
        "enabled": fd.enabled,
        "required": fd.required,
        "searchable": fd.searchable,
        "show_in_list": fd.show_in_list,
        "sort_order": fd.sort_order,
        "system": fd.system,
        "placeholder": fd.placeholder,
        "choices": [
            {"id": c.id, "value": c.value, "sort_order": c.sort_order, "active": c.active, "blocks_export": c.blocks_export}
            for c in fd.choices
            if with_inactive_choices or c.active
        ],
    }


@router.get("/fields")
def list_fields(entity: str | None = None, s: Session = Depends(get_db), _=Depends(require_admin)):
    q = select(FieldDefinition).where(FieldDefinition.active)
    if entity:
        q = q.where(FieldDefinition.entity == entity)
    q = q.order_by(FieldDefinition.entity, FieldDefinition.sort_order, FieldDefinition.id)
    return [field_out(fd, True) for fd in s.scalars(q)]


@router.post("/fields", status_code=201)
def create_field(body: FieldIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    if body.entity not in ENTITIES:
        raise HTTPException(422, f"entity must be one of {ENTITIES}")
    if body.type not in FIELD_TYPES:
        raise HTTPException(422, f"type must be one of {FIELD_TYPES}")
    existing = s.scalar(select(FieldDefinition).where(FieldDefinition.entity == body.entity, FieldDefinition.key == body.key))
    if existing is not None:
        if existing.active:
            raise HTTPException(409, "A field with this key already exists")
        # Re-create a deleted custom field: reuse the row so old values show up again.
        existing.active = True
        fd = existing
        _apply(fd, body)
    else:
        fd = FieldDefinition(**body.model_dump())
        s.add(fd)
    audit(s, admin, "field_create", detail=f"{body.entity}.{body.key}")
    s.commit()
    return field_out(fd, True)


@router.patch("/fields/{field_id}")
def update_field(field_id: int, body: FieldPatch, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    fd = s.get(FieldDefinition, field_id)
    if fd is None or not fd.active:
        _404("Field")
    if body.type is not None and body.type not in FIELD_TYPES:
        raise HTTPException(422, f"type must be one of {FIELD_TYPES}")
    if fd.system:
        forbidden = {"type", "enabled", "required"} & body.model_fields_set
        if forbidden:
            raise HTTPException(409, f"System field: cannot change {', '.join(sorted(forbidden))}")
    _apply(fd, body)
    audit(s, admin, "field_update", detail=f"{fd.entity}.{fd.key}")
    s.commit()
    return field_out(fd, True)


@router.delete("/fields/{field_id}")
def delete_field(field_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    """Custom fields only. Stored values are kept, so re-creating the field brings them back."""
    fd = s.get(FieldDefinition, field_id)
    if fd is None or not fd.active:
        _404("Field")
    if fd.system:
        raise HTTPException(409, "System fields cannot be deleted")
    fd.active = False
    audit(s, admin, "field_delete", detail=f"{fd.entity}.{fd.key}")
    s.commit()
    return {"result": "deleted"}


@router.post("/fields/{field_id}/choices", status_code=201)
def add_choice(field_id: int, body: ChoiceIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    fd = s.get(FieldDefinition, field_id)
    if fd is None or not fd.active:
        _404("Field")
    if fd.type not in ("choice", "suggest"):
        raise HTTPException(409, "Only choice and suggest fields have values")
    c = s.scalar(select(ChoiceValue).where(ChoiceValue.field_id == fd.id, ChoiceValue.value == body.value))
    if c is not None and c.active:
        raise HTTPException(409, "Value already exists")
    if c is None:
        c = ChoiceValue(field_id=fd.id, **body.model_dump())
        s.add(c)
    else:
        c.active = True
        _apply(c, body)
    audit(s, admin, "choice_add", detail=f"{fd.entity}.{fd.key}={body.value}")
    s.commit()
    return {"id": c.id, "value": c.value, "sort_order": c.sort_order, "active": c.active, "blocks_export": c.blocks_export}


@router.patch("/choices/{choice_id}")
def update_choice(choice_id: int, body: ChoicePatch, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    c = s.get(ChoiceValue, choice_id) or _404("Choice value")
    _apply(c, body)
    audit(s, admin, "choice_update", detail=f"{c.field.key}={c.value}")
    s.commit()
    return {"id": c.id, "value": c.value, "sort_order": c.sort_order, "active": c.active, "blocks_export": c.blocks_export}


# ---------------------------------------------------------------- settings and counters


class SettingsIn(BaseModel):
    mcam_root: str | None = None
    nc_root: str | None = None
    archive_subfolder: str | None = None
    company_name: str | None = Field(default=None, max_length=200)
    # naming of operation folders, NC header placeholders and the info file (see seed.NAMING_*)
    folder_prefix: str | None = Field(default=None, max_length=20, pattern=r"^[A-Za-z0-9_-]*$")
    placeholder_prefix: str | None = Field(default=None, min_length=1, max_length=20, pattern=r"^[A-Za-z0-9_]+$")
    info_file_extension: str | None = Field(default=None, max_length=10, pattern=r"^(\.[A-Za-z0-9]{1,9})?$")


class CounterIn(BaseModel):
    value: int = Field(ge=0)


@router.get("/settings")
def get_settings(s: Session = Depends(get_db), _=Depends(require_admin)):
    return {row.key: row.value for row in s.scalars(select(Setting))}


@router.put("/settings")
def put_settings(body: SettingsIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    for key in body.model_fields_set:
        value = getattr(body, key)
        if value is None:
            continue
        row = s.get(Setting, key)
        if row is None:
            s.add(Setting(key=key, value=value))
        else:
            row.value = value
        audit(s, admin, "setting_update", detail=f"{key}={value}")
    s.commit()
    return {row.key: row.value for row in s.scalars(select(Setting))}


@router.get("/counters")
def get_counters(s: Session = Depends(get_db), _=Depends(require_admin)):
    """value = last number handed out; the next one is value + 1."""
    return {c.name: c.value for c in s.scalars(select(Counter).order_by(Counter.name))}


@router.put("/counters/{name}")
def set_counter(name: str, body: CounterIn, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    """Set the last used number, e.g. to the highest number of a previous system so new ones never clash.
    Lowering it is allowed; numbers already in use are skipped when handing out."""
    c = s.get(Counter, name) or _404("Counter")
    c.value = body.value
    audit(s, admin, "counter_set", detail=f"{name}={body.value}")
    s.commit()
    return {name: c.value}


@router.get("/audit")
def get_audit(
    limit: int = 200,
    operation_id: int | None = None,
    article_id: int | None = None,
    s: Session = Depends(get_db),
    _=Depends(require_admin),
):
    q = select(AuditEntry).order_by(AuditEntry.id.desc()).limit(min(limit, 2000))
    if operation_id is not None:
        q = q.where(AuditEntry.operation_id == operation_id)
    if article_id is not None:
        q = q.where(AuditEntry.article_id == article_id)
    return [
        {
            "id": e.id,
            "time": e.time.isoformat(),
            "user_id": e.user_id,
            "article_id": e.article_id,
            "operation_id": e.operation_id,
            "action": e.action,
            "detail": e.detail,
        }
        for e in s.scalars(q)
    ]


def _404(what: str):
    raise HTTPException(404, f"{what} not found")
