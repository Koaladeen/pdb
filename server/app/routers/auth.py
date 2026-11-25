"""Login, own profile (password, picture) and the public user list for the login page."""
from datetime import timedelta
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import Response
from pydantic import BaseModel, Field
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from .. import pictures
from ..config import Config
from ..deps import current_token, current_user, get_config, get_db
from ..models import ApiToken, MachineRight, User, UserPreference, UserWorkspace, utcnow
from ..security import hash_password, new_token, token_hash, verify_password
from ..services import audit, user_out

from ..seed import DEFAULT_ADMIN_PASSWORD, must_change_password

router = APIRouter(tags=["auth"])


class LoginIn(BaseModel):
    username: str
    password: str = ""  # may be empty for users without a password
    pc_name: str = Field(default="", max_length=100)


@router.get("/auth/users")
def login_users(s: Session = Depends(get_db)):
    """For the login page: pick your name instead of typing it. No login needed; shows only
    names and whether a password is asked, never anything else."""
    users = s.scalars(select(User).where(User.active).order_by(func.lower(User.display_name)))
    return [
        {
            "username": u.username,
            "display_name": u.display_name,
            "id": u.id,
            "has_password": u.has_password,
            "avatar_at": pictures.avatars.updated_at(u.id),
        }
        for u in users
    ]


@router.post("/auth/login")
def login(body: LoginIn, s: Session = Depends(get_db), config: Config = Depends(get_config)):
    user = s.scalar(select(User).where(User.username == body.username))
    ok = user is not None and user.active and (
        not user.has_password or verify_password(body.password, user.password_hash)
    )
    if not ok:
        raise HTTPException(401, "Wrong username or password")
    token = new_token()
    s.add(
        ApiToken(
            token_hash=token_hash(token),
            user_id=user.id,
            pc_name=body.pc_name,
            expires_at=utcnow() + timedelta(hours=config.token_hours),
        )
    )
    audit(s, user, "login", detail=body.pc_name)
    s.commit()
    return {"token": token, "user": {**user_out(user), "must_change_password": must_change_password(user)}}


@router.post("/auth/logout")
def logout(tok: ApiToken = Depends(current_token), s: Session = Depends(get_db)):
    s.delete(tok)
    s.commit()
    return {"ok": True}


@router.get("/auth/me")
def me(user: User = Depends(current_user), s: Session = Depends(get_db)):
    machine_ids = list(s.scalars(select(MachineRight.machine_id).where(MachineRight.user_id == user.id)))
    return {
        **user_out(user),
        "must_change_password": must_change_password(user),
        "machine_ids": machine_ids,
        "preferences": _preferences(s, user.id),
        "workspace": _workspace(s, user.id),
    }


# ---------------------------------------------------------------- appearance (per user)


class Preferences(BaseModel):
    """Everything the user can change about the look. Unknown keys are dropped, wrong values
    rejected, so the web page can rely on these."""

    theme: Literal["auto", "light", "dark"] = "auto"
    accent: str = Field(default="#2b579a", pattern=r"^#[0-9a-fA-F]{6}$")
    size: Literal["s", "m", "l", "xl"] = "m"
    density: Literal["compact", "normal", "comfortable"] = "normal"
    corners: Literal["square", "normal", "round"] = "normal"
    header: Literal["colored", "plain"] = "colored"
    sidebar: bool = True
    pictures: bool = True
    start: Literal["search", "last"] = "search"
    motion: Literal["off", "subtle", "fancy"] = "subtle"
    language: Literal["auto", "de", "en", "it", "lt"] = "auto"
    # own arrangement of a page's blocks, e.g. {"article": [{"id": "operations", "span": 6, "hidden": false}, ...]}
    layouts: dict[str, list["LayoutBlock"]] = Field(default={}, max_length=10)


class LayoutBlock(BaseModel):
    id: str = Field(min_length=1, max_length=40, pattern=r"^[a-z0-9_-]+$")
    span: Literal[2, 3, 4, 6] = 6  # of 6 columns: third, half, two thirds, full width
    hidden: bool = False


Preferences.model_rebuild()


def _preferences(s: Session, user_id: int) -> dict:
    row = s.get(UserPreference, user_id)
    try:
        return Preferences(**(row.data if row else {})).model_dump()
    except ValueError:
        return Preferences().model_dump()  # stored values from an older version: defaults


# ---------------------------------------------------------------- workspace (open cards, recent searches)


class OpenCard(BaseModel):
    id: int
    title: str = Field(default="", max_length=300)
    subtitle: str = Field(default="", max_length=300)


class RecentSearch(BaseModel):
    filters: dict[str, str] = {}
    at: int = 0


class Workspace(BaseModel):
    cards: list[OpenCard] = Field(default=[], max_length=50)
    recent: list[RecentSearch] = Field(default=[], max_length=20)


def _workspace(s: Session, user_id: int) -> dict:
    row = s.get(UserWorkspace, user_id)
    try:
        return Workspace(**(row.data if row else {})).model_dump()
    except ValueError:
        return Workspace().model_dump()


@router.get("/auth/me/workspace")
def get_workspace(s: Session = Depends(get_db), user: User = Depends(current_user)):
    return _workspace(s, user.id)


@router.put("/auth/me/workspace")
def put_workspace(body: Workspace, s: Session = Depends(get_db), user: User = Depends(current_user)):
    row = s.get(UserWorkspace, user.id)
    if row is None:
        s.add(UserWorkspace(user_id=user.id, data=body.model_dump()))
    else:
        row.data = body.model_dump()
    s.commit()
    return body.model_dump()


@router.get("/auth/me/preferences")
def get_preferences(s: Session = Depends(get_db), user: User = Depends(current_user)):
    return _preferences(s, user.id)


@router.put("/auth/me/preferences")
def put_preferences(body: Preferences, s: Session = Depends(get_db), user: User = Depends(current_user)):
    row = s.get(UserPreference, user.id)
    if row is None:
        s.add(UserPreference(user_id=user.id, data=body.model_dump()))
    else:
        row.data = body.model_dump()
    s.commit()
    return body.model_dump()


class PasswordIn(BaseModel):
    current_password: str = ""
    new_password: str | None = Field(default=None, min_length=4)  # None = no password any more


@router.post("/auth/me/password")
def change_own_password(body: PasswordIn, s: Session = Depends(get_db), tok: ApiToken = Depends(current_token)):
    user = tok.user
    if user.has_password and not verify_password(body.current_password, user.password_hash):
        raise HTTPException(403, "Current password is wrong")
    if body.new_password is None and user.is_admin:
        raise HTTPException(409, "Admins need a password")
    if body.new_password == DEFAULT_ADMIN_PASSWORD:
        raise HTTPException(422, "Bitte ein anderes Passwort als das Standard-Passwort wählen")
    user.password_hash = hash_password(body.new_password) if body.new_password else ""
    # log out other sessions, keep this one
    s.query(ApiToken).filter(ApiToken.user_id == user.id, ApiToken.token_hash != tok.token_hash).delete()
    audit(s, user, "password_set" if body.new_password else "password_removed")
    s.commit()
    return user_out(user)


# ---------------------------------------------------------------- profile pictures


async def read_avatar(request: Request) -> bytes:
    data = await request.body()
    if len(data) > pictures.avatars.max_bytes:
        raise HTTPException(413, "Picture larger than 2 MB")
    if not pictures.media_type(data):
        raise HTTPException(415, "Only PNG or JPEG pictures")
    return data


@router.get("/users/{user_id}/avatar")
def get_avatar(user_id: int):
    """Public, so the login page can show it before anyone is logged in."""
    data = pictures.avatars.read(user_id)
    if data is None:
        raise HTTPException(404, "No picture")
    return Response(data, media_type=pictures.media_type(data) or "application/octet-stream",
                    headers={"Cache-Control": "public, max-age=86400"})


@router.put("/auth/me/avatar")
async def set_own_avatar(request: Request, s: Session = Depends(get_db), user: User = Depends(current_user)):
    pictures.avatars.save(user.id, await read_avatar(request))
    audit(s, user, "avatar_set")
    s.commit()
    return user_out(user)


@router.delete("/auth/me/avatar")
def delete_own_avatar(s: Session = Depends(get_db), user: User = Depends(current_user)):
    pictures.avatars.delete(user.id)
    audit(s, user, "avatar_delete")
    s.commit()
    return user_out(user)
