"""Request dependencies: database session, logged-in user, admin check."""
from collections.abc import Iterator
from datetime import datetime, timedelta

from fastapi import Depends, HTTPException, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.orm import Session

from .config import Config
from .models import ApiToken, User, utcnow
from .security import token_hash

_bearer = HTTPBearer(auto_error=False)


def get_config(request: Request) -> Config:
    return request.app.state.config


def get_db(request: Request) -> Iterator[Session]:
    yield from request.app.state.db.session()


def stale_before(config: Config = Depends(get_config)) -> datetime:
    """Locks whose last heartbeat is older than this are stale."""
    return utcnow() - timedelta(minutes=config.lock_timeout_minutes)


def current_token(
    creds: HTTPAuthorizationCredentials | None = Depends(_bearer),
    s: Session = Depends(get_db),
) -> ApiToken:
    if creds is None:
        raise HTTPException(401, "Not logged in", headers={"WWW-Authenticate": "Bearer"})
    tok = s.get(ApiToken, token_hash(creds.credentials))
    if tok is None or tok.expires_at < utcnow() or not tok.user.active:
        raise HTTPException(401, "Login expired", headers={"WWW-Authenticate": "Bearer"})
    return tok


def current_user(tok: ApiToken = Depends(current_token)) -> User:
    return tok.user


def require_admin(user: User = Depends(current_user)) -> User:
    if not user.is_admin:
        raise HTTPException(403, "Admin only")
    return user
