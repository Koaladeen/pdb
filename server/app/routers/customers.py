"""Customers. Everyone can look up, add and edit; only admins delete (or deactivate if used)."""
from collections import Counter as Tally

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from ..deps import current_user, get_db, require_admin
from ..models import Article, Customer, FieldDefinition, User
from ..services import audit

router = APIRouter(prefix="/customers", tags=["customers"])


class CustomerIn(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    number: str = ""
    contact: str = ""
    phone: str = ""
    email: str = ""
    address: str = ""
    notes: str = ""


class CustomerPatch(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=200)
    number: str | None = None
    contact: str | None = None
    phone: str | None = None
    email: str | None = None
    address: str | None = None
    notes: str | None = None
    active: bool | None = None


def customer_out(c: Customer, parts: int | None = None) -> dict:
    out = {
        "id": c.id,
        "name": c.name,
        "number": c.number,
        "contact": c.contact,
        "phone": c.phone,
        "email": c.email,
        "address": c.address,
        "notes": c.notes,
        "active": c.active,
    }
    if parts is not None:
        out["parts"] = parts
    return out


def part_counts(s: Session) -> Tally:
    """How many parts reference each customer (over all customer-type fields)."""
    tally: Tally = Tally()
    keys = s.scalars(
        select(FieldDefinition.key).where(FieldDefinition.entity == "article", FieldDefinition.type == "customer")
    )
    for key in keys:
        col = Article.data[key].as_integer()
        for cid, n in s.execute(select(col, func.count()).where(col.is_not(None)).group_by(col)):
            tally[cid] += n
    return tally


def _check_name(s: Session, name: str, own_id: int | None = None) -> str:
    name = name.strip()
    if not name:
        raise HTTPException(422, "Name is required")
    other = s.scalar(select(Customer).where(func.lower(Customer.name) == name.lower()))
    if other is not None and other.id != own_id:
        raise HTTPException(409, f"Customer '{other.name}' already exists")
    return name


@router.get("")
def list_customers(include_inactive: bool = False, s: Session = Depends(get_db), _: User = Depends(current_user)):
    q = select(Customer).order_by(func.lower(Customer.name))
    if not include_inactive:
        q = q.where(Customer.active)
    counts = part_counts(s)
    return [customer_out(c, counts.get(c.id, 0)) for c in s.scalars(q)]


@router.post("", status_code=201)
def create_customer(body: CustomerIn, s: Session = Depends(get_db), user: User = Depends(current_user)):
    c = Customer(**{**body.model_dump(), "name": _check_name(s, body.name)})
    s.add(c)
    s.flush()
    audit(s, user, "customer_create", detail=c.name)
    s.commit()
    return customer_out(c, 0)


@router.patch("/{customer_id}")
def update_customer(customer_id: int, body: CustomerPatch, s: Session = Depends(get_db), user: User = Depends(current_user)):
    c = s.get(Customer, customer_id)
    if c is None:
        raise HTTPException(404, "Customer not found")
    if body.active is not None and not user.is_admin:
        raise HTTPException(403, "Only admins can deactivate customers")
    for key in body.model_fields_set:
        value = getattr(body, key)
        if value is None:
            continue
        setattr(c, key, _check_name(s, value, c.id) if key == "name" else value)
    audit(s, user, "customer_update", detail=c.name)
    s.commit()
    return customer_out(c, part_counts(s).get(c.id, 0))


@router.delete("/{customer_id}")
def delete_customer(customer_id: int, s: Session = Depends(get_db), admin: User = Depends(require_admin)):
    """Deletes an unused customer; one that parts still use is only deactivated."""
    c = s.get(Customer, customer_id)
    if c is None:
        raise HTTPException(404, "Customer not found")
    if part_counts(s).get(c.id, 0):
        c.active = False
        result = "deactivated"
    else:
        s.delete(c)
        result = "deleted"
    audit(s, admin, f"customer_{result}", detail=c.name)
    s.commit()
    return {"result": result}
