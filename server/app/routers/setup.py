"""First-run setup of a NEW installation. An empty database gets the built-in admin (admin /
admin, see seed.py) and "setup_pending". The first admin login then runs this wizard: new admin
password and name, folders, fields, machines, first MDM number. Only once; afterwards the same
things are changed in the admin screens."""
import os
import re
from datetime import timedelta
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.orm import Session

from ..config import Config
from ..deps import get_config, get_db, require_admin
from ..models import NC_NAMING, ApiToken, Counter, FieldDefinition, Machine, Setting, User, utcnow
from ..security import hash_password, new_token, token_hash
from ..seed import DEFAULT_ADMIN_PASSWORD
from ..services import audit, nc_counter_name, user_out

router = APIRouter(prefix="/setup", tags=["setup"])

# "Einfach": only these fields are switched on; all others stay available in the admin screen.
SIMPLE_FIELDS = {
    "article": {"teilename", "zeichnungsnummer", "artikelnummer", "kunde", "status", "notiz"},
    "operation": {"prozessbeschreibung", "cam_programmierer", "operations_status", "material", "kommentar"},
}
_FOLDER_BAD = re.compile(r'[<>:"/\\|?*]')


class SetupAdmin(BaseModel):
    username: str = Field(default="", max_length=100)  # empty = keep "admin"
    display_name: str = Field(min_length=1, max_length=200)
    password: str = Field(min_length=6)  # replaces the default password


class SetupFolders(BaseModel):
    mcam_root: str = Field(min_length=3, max_length=500)
    nc_root: str = Field(min_length=3, max_length=500)


class SetupMachine(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    mcam_folder: str = Field(min_length=1, max_length=200)
    nc_folder: str = Field(min_length=1, max_length=200)
    nc_naming: str = "mdm"
    nc_extension: str = Field(default=".nc", pattern=r"^\.[A-Za-z0-9]{1,10}$")
    machine_definition: str = ""


class SetupIn(BaseModel):
    company: str = Field(default="", max_length=200)
    admin: SetupAdmin
    folders: SetupFolders
    fields: Literal["full", "simple", "custom"] = "full"
    # chosen field by field in the wizard: {"article": [keys], "operation": [keys]}; system fields stay on
    enabled_fields: dict[str, list[str]] | None = None
    machines: list[SetupMachine] = Field(default=[], max_length=200)
    mdm_start: int = Field(default=0, ge=0)


def _needed(s: Session) -> bool:
    row = s.get(Setting, "setup_pending")
    return row is not None and row.value == "1"


@router.get("/status")
def status(s: Session = Depends(get_db)):
    company = s.get(Setting, "company_name")
    # a local test install (try-local.ps1) suggests folders for the wizard
    suggest = {"mcam_root": os.environ.get("PDB_SUGGEST_MCAM_ROOT", ""), "nc_root": os.environ.get("PDB_SUGGEST_NC_ROOT", "")}
    needed = _needed(s)
    out = {"needed": needed, "company": company.value if company else ""}
    if needed:
        out["suggest"] = suggest
    return out


@router.post("", status_code=201)
def run_setup(body: SetupIn, admin: User = Depends(require_admin), s: Session = Depends(get_db),
              config: Config = Depends(get_config)):
    if not _needed(s):
        raise HTTPException(409, "PDB ist bereits eingerichtet")
    if body.admin.password == DEFAULT_ADMIN_PASSWORD:
        raise HTTPException(422, "Bitte ein anderes Passwort als das Standard-Passwort wählen")
    username = body.admin.username.strip() or admin.username
    if username != admin.username and s.scalar(select(User).where(User.username == username)):
        raise HTTPException(409, f"Benutzername {username} ist schon vergeben")
    names = set()
    for m in body.machines:
        for folder in (m.mcam_folder, m.nc_folder):
            if _FOLDER_BAD.search(folder):
                raise HTTPException(422, f"Maschine {m.name}: Ordnername ohne \\ / : * ? \" < > | angeben")
        if m.nc_naming not in NC_NAMING:
            raise HTTPException(422, f"Maschine {m.name}: NC-Dateiname muss {NC_NAMING} sein")
        if m.name.lower() in names:
            raise HTTPException(422, f"Maschine {m.name} doppelt")
        names.add(m.name.lower())

    admin.username = username
    admin.display_name = body.admin.display_name.strip()
    admin.password_hash = hash_password(body.admin.password)
    s.query(ApiToken).filter(ApiToken.user_id == admin.id).delete()  # old sessions used the default password
    s.get(Setting, "setup_pending").value = "0"
    for key, value in (("mcam_root", body.folders.mcam_root.strip()), ("nc_root", body.folders.nc_root.strip()),
                       ("company_name", body.company.strip())):
        row = s.get(Setting, key)
        if row is None:
            s.add(Setting(key=key, value=value))
        else:
            row.value = value
    chosen = {e: set(keys) for e, keys in body.enabled_fields.items()} if body.enabled_fields is not None else None
    if chosen is None and body.fields == "simple":
        chosen = SIMPLE_FIELDS
    if chosen is not None:
        for fd in s.scalars(select(FieldDefinition).where(FieldDefinition.system.is_(False))):
            fd.enabled = fd.key in chosen.get(fd.entity, set())
    for m in body.machines:
        machine = Machine(**m.model_dump())
        s.add(machine)
        s.flush()
        s.add(Counter(name=nc_counter_name(machine.id), value=0))
    counter = s.get(Counter, "mdm") or Counter(name="mdm", value=0)
    counter.value = body.mdm_start
    s.merge(counter)

    token = new_token()
    s.add(ApiToken(token_hash=token_hash(token), user_id=admin.id, pc_name="Einrichtung",
                   expires_at=utcnow() + timedelta(hours=config.token_hours)))
    audit(s, admin, "setup", detail=f"{body.fields}, {len(body.machines)} Maschinen")
    s.commit()
    return {"token": token, "user": user_out(admin)}
