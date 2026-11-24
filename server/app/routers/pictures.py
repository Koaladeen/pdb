from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import Response
from sqlalchemy.orm import Session

from .. import pictures
from ..deps import current_user, get_db
from ..models import User
from ..services import audit, require_machine_right
from .articles import get_operation

router = APIRouter(tags=["pictures"])


@router.get("/operations/{operation_id}/picture")
def get_picture(operation_id: int, s: Session = Depends(get_db), _: User = Depends(current_user)):
    get_operation(s, operation_id)
    try:
        data = pictures.path_for(operation_id).read_bytes()
    except FileNotFoundError:
        raise HTTPException(404, "No picture") from None
    return Response(data, media_type=pictures.media_type(data) or "application/octet-stream",
                    headers={"Cache-Control": "private, max-age=86400"})


@router.put("/operations/{operation_id}/picture")
async def put_picture(operation_id: int, request: Request, s: Session = Depends(get_db), user: User = Depends(current_user)):
    """Body: the raw PNG or JPEG bytes (Content-Type is not trusted; the bytes are checked)."""
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    data = await request.body()
    if len(data) > pictures.MAX_BYTES:
        raise HTTPException(413, f"Picture larger than {pictures.MAX_BYTES // (1024 * 1024)} MB")
    if not pictures.media_type(data):
        raise HTTPException(415, "Only PNG or JPEG pictures")
    pictures.save(operation_id, data)
    audit(s, user, "picture_set", article_id=op.article_id, operation_id=op.id, detail=f"{len(data)} bytes")
    s.commit()
    return {"picture_at": pictures.updated_at(operation_id)}


@router.delete("/operations/{operation_id}/picture")
def delete_picture(operation_id: int, s: Session = Depends(get_db), user: User = Depends(current_user)):
    op = get_operation(s, operation_id)
    require_machine_right(s, user, op.machine_id)
    if not pictures.delete(operation_id):
        raise HTTPException(404, "No picture")
    audit(s, user, "picture_delete", article_id=op.article_id, operation_id=op.id)
    s.commit()
    return {"result": "deleted"}
