"""Tables. System fields are real columns; configurable fields live in the JSON `data` column
and are described by FieldDefinition rows."""
from datetime import UTC, datetime
from typing import Any

from sqlalchemy import (
    JSON,
    Boolean,
    DateTime,
    ForeignKey,
    Integer,
    String,
    Text,
    UniqueConstraint,
)
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .db import Base


def utcnow() -> datetime:
    """Naive UTC; SQLite has no time zones, so everything is stored as UTC without tzinfo."""
    return datetime.now(UTC).replace(tzinfo=None)


ROLES = ("user", "admin")


class User(Base):
    __tablename__ = "users"
    id: Mapped[int] = mapped_column(primary_key=True)
    username: Mapped[str] = mapped_column(String(100), unique=True)
    display_name: Mapped[str] = mapped_column(String(200))
    password_hash: Mapped[str] = mapped_column(String(300))
    role: Mapped[str] = mapped_column(String(20), default="user")
    active: Mapped[bool] = mapped_column(Boolean, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)

    @property
    def is_admin(self) -> bool:
        return self.role == "admin"

    @property
    def has_password(self) -> bool:
        """Empty hash = login by picking the name only. Admins always need a password."""
        return bool(self.password_hash)


class UserPreference(Base):
    """Appearance settings per user (colours, size, density, ...), so they follow the user to
    every PC. Own table: new installs and existing databases get it without a migration."""

    __tablename__ = "user_preferences"
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), primary_key=True)
    data: Mapped[dict[str, Any]] = mapped_column(JSON, default=dict)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow, onupdate=utcnow)


class UserWorkspace(Base):
    """What the user had open: cards in the sidebar and recent searches. On the server so it
    follows the user to every PC (and another user on the same PC sees their own)."""

    __tablename__ = "user_workspaces"
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), primary_key=True)
    data: Mapped[dict[str, Any]] = mapped_column(JSON, default=dict)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow, onupdate=utcnow)


class ApiToken(Base):
    __tablename__ = "api_tokens"
    token_hash: Mapped[str] = mapped_column(String(64), primary_key=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"))
    pc_name: Mapped[str] = mapped_column(String(100), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(DateTime)
    user: Mapped[User] = relationship()


class Machine(Base):
    __tablename__ = "machines"
    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(200), unique=True)  # e.g. "DMU 50"
    mcam_folder: Mapped[str] = mapped_column(String(200))  # under the Mastercam main folder
    nc_folder: Mapped[str] = mapped_column(String(200))  # under the NC export main folder
    nc_naming: Mapped[str] = mapped_column(String(10), default="mdm")  # "mdm" or "nc"
    nc_extension: Mapped[str] = mapped_column(String(20), default=".nc")
    nc_number_max: Mapped[int | None] = mapped_column(Integer, nullable=True)
    machine_definition: Mapped[str] = mapped_column(String(500), default="")
    post: Mapped[str] = mapped_column(String(500), default="")
    header_template: Mapped[str] = mapped_column(Text, default="")
    active: Mapped[bool] = mapped_column(Boolean, default=True)


NC_NAMING = ("mdm", "nc")


class MachineRight(Base):
    __tablename__ = "machine_rights"
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), primary_key=True)
    machine_id: Mapped[int] = mapped_column(
        ForeignKey("machines.id", ondelete="CASCADE"), primary_key=True
    )


class Counter(Base):
    """Number sequences. `value` is the last number handed out; the next one is value + 1.
    Names: "mdm" for the PDB number (historic name), "nc:<machine id>" for each machine's NC program number."""

    __tablename__ = "counters"
    name: Mapped[str] = mapped_column(String(50), primary_key=True)
    value: Mapped[int] = mapped_column(Integer, default=0)


class Setting(Base):
    __tablename__ = "settings"
    key: Mapped[str] = mapped_column(String(100), primary_key=True)
    value: Mapped[str] = mapped_column(Text, default="")


FIELD_TYPES = (
    "text",  # single line
    "longtext",  # notes
    "suggest",  # free text, the UI suggests choice values and earlier entries
    "choice",  # must be one of the active choice values
    "integer",
    "number",
    "date",  # YYYY-MM-DD
    "duration",  # HH:MM:SS
    "user",  # user id
    "customer",  # customer id (customers table)
    "boolean",
)
ENTITIES = ("article", "operation")


class FieldDefinition(Base):
    __tablename__ = "field_definitions"
    __table_args__ = (UniqueConstraint("entity", "key"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    entity: Mapped[str] = mapped_column(String(20))
    key: Mapped[str] = mapped_column(String(100))
    label: Mapped[str] = mapped_column(String(200))
    type: Mapped[str] = mapped_column(String(20), default="text")
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    required: Mapped[bool] = mapped_column(Boolean, default=False)
    searchable: Mapped[bool] = mapped_column(Boolean, default=False)
    show_in_list: Mapped[bool] = mapped_column(Boolean, default=False)
    sort_order: Mapped[int] = mapped_column(Integer, default=0)
    # System fields are stored as table columns and cannot be disabled or deleted.
    system: Mapped[bool] = mapped_column(Boolean, default=False)
    # Name used in NC headers, e.g. "PDB_Partname" for #PDB_Partname.
    placeholder: Mapped[str | None] = mapped_column(String(100), nullable=True)
    # False = deleted custom field; values stay in the data but the field is gone everywhere.
    active: Mapped[bool] = mapped_column(Boolean, default=True)
    choices: Mapped[list["ChoiceValue"]] = relationship(
        back_populates="field", order_by="ChoiceValue.sort_order", cascade="all, delete-orphan"
    )


class ChoiceValue(Base):
    __tablename__ = "choice_values"
    __table_args__ = (UniqueConstraint("field_id", "value"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    field_id: Mapped[int] = mapped_column(ForeignKey("field_definitions.id", ondelete="CASCADE"))
    value: Mapped[str] = mapped_column(String(200))
    sort_order: Mapped[int] = mapped_column(Integer, default=0)
    active: Mapped[bool] = mapped_column(Boolean, default=True)
    # For status fields: an operation with this value cannot be NC-exported ("Gesperrt").
    blocks_export: Mapped[bool] = mapped_column(Boolean, default=False)
    field: Mapped[FieldDefinition] = relationship(back_populates="choices")


class Customer(Base):
    """Customers, picked on the part card instead of typing the name each time."""

    __tablename__ = "customers"
    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(200), unique=True)
    number: Mapped[str] = mapped_column(String(100), default="")  # Kundennummer
    contact: Mapped[str] = mapped_column(String(200), default="")
    phone: Mapped[str] = mapped_column(String(100), default="")
    email: Mapped[str] = mapped_column(String(200), default="")
    address: Mapped[str] = mapped_column(Text, default="")
    notes: Mapped[str] = mapped_column(Text, default="")
    active: Mapped[bool] = mapped_column(Boolean, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Article(Base):
    """A part card. Holds the card fields; its operations hold the files."""

    __tablename__ = "articles"
    id: Mapped[int] = mapped_column(primary_key=True)
    data: Mapped[dict[str, Any]] = mapped_column(JSON, default=dict)
    created_by: Mapped[int | None] = mapped_column(ForeignKey("users.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow, onupdate=utcnow)
    operations: Mapped[list["Operation"]] = relationship(
        back_populates="article", order_by="Operation.op_number"
    )


class Operation(Base):
    """One Prozessdaten row = one Mastercam file = one NC program."""

    __tablename__ = "operations"
    __table_args__ = (UniqueConstraint("machine_id", "nc_number"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    article_id: Mapped[int] = mapped_column(ForeignKey("articles.id"))
    mdm_number: Mapped[int] = mapped_column(Integer, unique=True)
    op_number: Mapped[int] = mapped_column(Integer)
    nc_number: Mapped[int | None] = mapped_column(Integer, nullable=True)
    machine_id: Mapped[int] = mapped_column(ForeignKey("machines.id"))
    current_revision: Mapped[int] = mapped_column(Integer, default=0)
    data: Mapped[dict[str, Any]] = mapped_column(JSON, default=dict)
    created_by: Mapped[int | None] = mapped_column(ForeignKey("users.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow, onupdate=utcnow)
    article: Mapped[Article] = relationship(back_populates="operations")
    machine: Mapped[Machine] = relationship()


class Revision(Base):
    __tablename__ = "revisions"
    __table_args__ = (UniqueConstraint("operation_id", "number"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    operation_id: Mapped[int] = mapped_column(ForeignKey("operations.id", ondelete="CASCADE"))
    number: Mapped[int] = mapped_column(Integer)
    archive_path: Mapped[str] = mapped_column(String(1000))
    saved_by: Mapped[int] = mapped_column(ForeignKey("users.id"))
    saved_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    comment: Mapped[str] = mapped_column(Text, default="")


class ImportedFile(Base):
    """An existing Mastercam file taken over by the import: where it came from. The original stays
    untouched; it is copied into the operation folder by the server (if it can reach it) or by the
    PDB Helper the first time the operation is opened."""

    __tablename__ = "imported_files"
    id: Mapped[int] = mapped_column(primary_key=True)
    operation_id: Mapped[int] = mapped_column(ForeignKey("operations.id", ondelete="CASCADE"), unique=True)
    source_path: Mapped[str] = mapped_column(String(1000), index=True)
    size: Mapped[int | None] = mapped_column(Integer, nullable=True)
    modified: Mapped[str] = mapped_column(String(40), default="")
    status: Mapped[str] = mapped_column(String(20), default="pending")  # pending | copied
    imported_by: Mapped[int | None] = mapped_column(ForeignKey("users.id"), nullable=True)
    imported_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    copied_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    copied_by: Mapped[str] = mapped_column(String(100), default="")  # "server" or the PC name


class Lock(Base):
    """At most one row per operation; the primary key makes taking a lock atomic."""

    __tablename__ = "locks"
    operation_id: Mapped[int] = mapped_column(
        ForeignKey("operations.id", ondelete="CASCADE"), primary_key=True
    )
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"))
    pc_name: Mapped[str] = mapped_column(String(100))
    locked_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    last_heartbeat: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    user: Mapped[User] = relationship()


class AuditEntry(Base):
    __tablename__ = "audit"
    id: Mapped[int] = mapped_column(primary_key=True)
    time: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    user_id: Mapped[int | None] = mapped_column(ForeignKey("users.id"), nullable=True)
    article_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    operation_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    action: Mapped[str] = mapped_column(String(50))
    detail: Mapped[str] = mapped_column(Text, default="")
