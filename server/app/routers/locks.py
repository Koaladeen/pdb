"""Check-out with an advisory lock, heartbeat, check-in with revision, release, break-lock.

Rules (from the plan):
1. Taking a lock is one INSERT against the primary key on operation_id - never read-then-write.
2. A lock without heartbeat for PDB_LOCK_TIMEOUT_MINUTES is stale and can be taken over.
3. The add-in saves to a temp file and renames; the server only records the revision.
4. Locks are advisory, like MDM: the share itself is not blocked.
"""
from datetime import datetime

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import delete, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from ..deps import current_token, current_user, get_db, require_admin, stale_before
from ..models import ApiToken, Lock, Operation, Revision, User, utcnow
from ..services import audit, lock_out, operation_paths, require_machine_right
from .articles import get_operation

router = APIRouter(tags=["locks"])


class PcIn(BaseModel):
    pc_name: str = Field(default="", max_length=100)


class CheckinIn(PcIn):
    revision: int = Field(ge=1)
    comment: str = ""
    keep_lock: bool = False


def _pc(body: PcIn, tok: ApiToken) -> str:
    return body.pc_name or tok.pc_name or "?"


def _held_lock(s: Session, op: Operation, user: User, pc: str, cutoff: datetime) -> Lock:
    lock = s.get(Lock, op.id)
    if lock is None or lock.user_id != user.id or lock.pc_name != pc:
        raise HTTPException(
            409, {"message": "You do not hold the lock for this operation", "lock": lock_out(lock, cutoff)}
        )
    return lock


@router.post("/operations/{operation_id}/checkout")
def checkout(
    operation_id: int,
    body: PcIn,
    s: Session = Depends(get_db),
    tok: ApiToken = Depends(current_token),
    cutoff: datetime = Depends(stale_before),
):
    """Takes the lock. 409 with the holder if someone else has it: the add-in then offers
    read-only. Calling it again from the same user and PC just refreshes the lock."""
    user = tok.user
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    pc = _pc(body, tok)

    taken_over = s.execute(
        delete(Lock).where(Lock.operation_id == op.id, Lock.last_heartbeat < cutoff).returning(Lock.user_id, Lock.pc_name)
    ).first()
    if taken_over:
        audit(s, user, "lock_takeover", article_id=op.article_id, operation_id=op.id, detail=f"stale lock of user {taken_over[0]} on {taken_over[1]}")

    def refused(holder: Lock | None) -> HTTPException:
        return HTTPException(
            409,
            {
                "message": "Checked out by someone else; open read-only",
                "lock": lock_out(holder, cutoff),
                "paths": operation_paths(s, op),
            },
        )

    existing = s.get(Lock, op.id)
    if existing is not None:
        if existing.user_id == user.id and existing.pc_name == pc:
            existing.last_heartbeat = utcnow()
            s.commit()
            return {"locked": True, "lock": lock_out(existing, cutoff), "paths": operation_paths(s, op), "current_revision": op.current_revision}
        s.rollback()
        raise refused(s.get(Lock, op.id))

    # Two clients can both get here; the primary key lets exactly one INSERT succeed.
    now = utcnow()
    s.add(Lock(operation_id=op.id, user_id=user.id, pc_name=pc, locked_at=now, last_heartbeat=now))
    audit(s, user, "checkout", article_id=op.article_id, operation_id=op.id, detail=pc)
    try:
        s.commit()
    except IntegrityError:
        s.rollback()
        raise refused(s.get(Lock, op.id)) from None
    lock = s.get(Lock, op.id)
    return {"locked": True, "lock": lock_out(lock, cutoff), "paths": operation_paths(s, op), "current_revision": op.current_revision}


@router.post("/operations/{operation_id}/heartbeat")
def heartbeat(
    operation_id: int,
    body: PcIn,
    s: Session = Depends(get_db),
    tok: ApiToken = Depends(current_token),
    cutoff: datetime = Depends(stale_before),
):
    op = get_operation(s, operation_id)
    lock = _held_lock(s, op, tok.user, _pc(body, tok), cutoff)
    lock.last_heartbeat = utcnow()
    s.commit()
    return {"ok": True, "lock": lock_out(lock, cutoff)}


@router.post("/operations/{operation_id}/checkin")
def checkin(
    operation_id: int,
    body: CheckinIn,
    s: Session = Depends(get_db),
    tok: ApiToken = Depends(current_token),
    cutoff: datetime = Depends(stale_before),
):
    """Records a saved version. Before calling, the add-in has written the file (temp + rename)
    and copied it to paths.next_archive from checkout. `revision` must be current + 1, so a
    stale client cannot overwrite someone else's revision number."""
    user = tok.user
    op = get_operation(s, operation_id)
    lock = _held_lock(s, op, user, _pc(body, tok), cutoff)
    if body.revision != op.current_revision + 1:
        raise HTTPException(409, f"Expected revision {op.current_revision + 1}, got {body.revision}")
    archive = operation_paths(s, op, next_revision=body.revision)["next_archive"]
    s.add(Revision(operation_id=op.id, number=body.revision, archive_path=archive, saved_by=user.id, comment=body.comment))
    op.current_revision = body.revision
    if body.keep_lock:
        lock.last_heartbeat = utcnow()
    else:
        s.delete(lock)
    audit(s, user, "checkin", article_id=op.article_id, operation_id=op.id, detail=f"r{body.revision} {body.comment}".strip())
    s.commit()
    return {"revision": body.revision, "archive_path": archive, "locked": body.keep_lock, "paths": operation_paths(s, op)}


@router.post("/operations/{operation_id}/release")
def release(
    operation_id: int,
    body: PcIn,
    s: Session = Depends(get_db),
    tok: ApiToken = Depends(current_token),
    cutoff: datetime = Depends(stale_before),
):
    """Gives the lock back without a new version (file closed without saving)."""
    op = get_operation(s, operation_id)
    lock = _held_lock(s, op, tok.user, _pc(body, tok), cutoff)
    s.delete(lock)
    audit(s, tok.user, "release", article_id=op.article_id, operation_id=op.id)
    s.commit()
    return {"ok": True}


@router.post("/admin/break-lock/{operation_id}")
def break_lock(
    operation_id: int,
    s: Session = Depends(get_db),
    admin: User = Depends(require_admin),
    cutoff: datetime = Depends(stale_before),
):
    op = get_operation(s, operation_id)
    lock = s.get(Lock, op.id)
    if lock is None:
        raise HTTPException(404, "Operation is not locked")
    detail = f"{lock.user.display_name} on {lock.pc_name} since {lock.locked_at.isoformat()}"
    s.delete(lock)
    audit(s, admin, "break_lock", article_id=op.article_id, operation_id=op.id, detail=detail)
    s.commit()
    return {"ok": True, "was": detail}


@router.get("/locks")
def list_locks(s: Session = Depends(get_db), _: User = Depends(current_user), cutoff: datetime = Depends(stale_before)):
    out = []
    for lock in s.scalars(select(Lock).order_by(Lock.locked_at)):
        op = s.get(Operation, lock.operation_id)
        out.append(
            {
                "operation_id": op.id,
                "mdm_number": op.mdm_number,
                "machine": op.machine.name,
                "part": op.article.data.get("teilename", ""),
                **lock_out(lock, cutoff),
            }
        )
    return out


@router.get("/operations/{operation_id}/revisions")
def revisions(operation_id: int, s: Session = Depends(get_db), _: User = Depends(current_user)):
    """Every saved version with its archive copy. To restore: check out, copy the archive file
    over the working file, check in as a new revision."""
    op = get_operation(s, operation_id)
    names = {u.id: u.display_name for u in s.scalars(select(User))}
    rows = s.scalars(select(Revision).where(Revision.operation_id == op.id).order_by(Revision.number.desc()))
    return [
        {
            "number": r.number,
            "archive_path": r.archive_path,
            "saved_by": names.get(r.saved_by, "?"),
            "saved_at": r.saved_at.isoformat(),
            "comment": r.comment,
        }
        for r in rows
    ]
