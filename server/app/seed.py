"""First-start data: admin user, settings, counters and the field catalogue.
Runs on every start but only adds what is missing, so admin changes are never overwritten."""
from sqlalchemy import select
from sqlalchemy.orm import Session

from .config import Config
from .models import Article, ChoiceValue, Counter, Customer, FieldDefinition, Operation, Setting, User
from .security import hash_password, verify_password

# Folders are set in the setup wizard (or admin settings); nothing company-specific here.
DEFAULT_SETTINGS = {
    "mcam_root": "",
    "nc_root": "",
    "archive_subfolder": "Revisionen",
    "company_name": "",
}

# How operation folders, NC header placeholders and the info file next to an exported program are
# named (admin settings). New installations get neutral names; one that already has operations keeps
# the names its folders and posts were built with (e.g. taken over from another system).
NAMING_NEW = {"folder_prefix": "PDB", "placeholder_prefix": "PDB_", "info_file_extension": ""}
NAMING_EXISTING = {"folder_prefix": "MDM", "placeholder_prefix": "MDM_", "info_file_extension": ".MDM"}

# (key, label, type, flags, placeholder) - flags: r=required, s=searchable, l=show in list, S=system.
# Placeholder without prefix: the post writes #<placeholder_prefix><name> into the NC header.
ARTICLE_FIELDS = [
    ("teilename", "Teilename", "suggest", "rsl", "Partname"),
    ("zeichnungsnummer", "Zeichnungsnummer", "suggest", "sl", "DrawingNumber"),
    ("bestellnummer", "Bestellnummer", "suggest", "sl", None),
    ("index_version", "Index / Version", "text", "s", None),
    ("stueckzahl", "Stückzahl", "integer", "", None),
    ("artikelnummer", "Artikelnummer", "suggest", "sl", None),
    ("kunde", "Kunde", "customer", "sl", "Customer"),
    ("projektnummer", "Projektnummer", "suggest", "s", None),
    ("auftragsnummer", "Auftragsnummer", "text", "s", None),
    ("rohteil_material", "Rohteil-Material", "suggest", "", None),
    ("rohteil_art", "Rohteil-Art", "suggest", "", None),
    ("rohteil_masse", "Rohteil-Maße", "text", "", None),
    ("profile", "Profile", "text", "", None),
    ("dimension", "Dimension", "text", "", None),
    ("status", "Status", "choice", "sl", None),
    ("startdatum_auftrag", "Startdatum Auftrag", "date", "", None),
    ("lieferdatum_auftrag", "Lieferdatum Auftrag", "date", "", None),
    ("notiz", "Notiz", "longtext", "", None),
]

OPERATION_FIELDS = [
    ("op_number", "Nr.", "integer", "rlS", None),
    ("mdm_number", "PDB-Nr.", "integer", "slS", "FN"),
    ("nc_number", "NC-Programmnummer", "integer", "slS", None),
    ("machine_id", "Maschine", "integer", "rslS", None),
    ("prozessbeschreibung", "Prozessbeschreibung", "suggest", "sl", "OperationTitle"),
    ("cam_programmierer", "CAM-Programmierer", "user", "sl", "Programmer"),
    ("cnc_bediener", "CNC-Bediener", "user", "", None),
    ("operations_status", "Operations-Status", "choice", "sl", None),
    ("fixture", "Fixture", "suggest", "", None),
    ("fixt_location", "Fixt. Location", "suggest", "", None),
    ("material", "Material", "suggest", "", None),
    ("dimension", "Dimension", "text", "", None),
    ("programmierzeit", "Programmierzeit", "duration", "", None),
    ("einrichtezeit", "Einrichtezeit", "duration", "", None),
    ("umspannzeit", "Umspannzeit", "duration", "", None),
    ("runtime", "Runtime", "duration", "", None),
    ("unmanned", "Unmanned", "duration", "", None),
    ("kommentar", "Kommentar", "longtext", "", None),
    ("cam_system", "CAM-System", "text", "", None),
]

# Status values: released = good to produce, locked = program blocked (no NC export).
STATUS_CHOICES = [("Freigegeben", False), ("Gesperrt", True)]


def _seed_fields(s: Session, entity: str, fields: list, prefix: str) -> None:
    existing = {f.key for f in s.scalars(select(FieldDefinition).where(FieldDefinition.entity == entity))}
    for order, (key, label, ftype, flags, placeholder) in enumerate(fields):
        if key in existing:
            continue
        fd = FieldDefinition(
            entity=entity,
            key=key,
            label=label,
            type=ftype,
            required="r" in flags,
            searchable="s" in flags,
            show_in_list="l" in flags,
            system="S" in flags,
            placeholder=f"{prefix}{placeholder}" if placeholder else None,
            sort_order=order * 10,
        )
        if key in ("status", "operations_status"):
            fd.choices = [
                ChoiceValue(value=v, sort_order=i, blocks_export=b) for i, (v, b) in enumerate(STATUS_CHOICES)
            ]
        s.add(fd)


def _seed_naming(s: Session) -> None:
    if s.get(Setting, "folder_prefix") is not None:
        return
    fresh = s.scalar(select(FieldDefinition.id).limit(1)) is None
    has_operations = s.scalar(select(Operation.id).limit(1)) is not None
    for key, value in (NAMING_EXISTING if has_operations else NAMING_NEW).items():
        s.add(Setting(key=key, value=value))
    if not fresh and not has_operations:
        # set up with an earlier version but nothing produced yet: no posts or folders depend on
        # the old names, so switch the field placeholders to the neutral ones as well
        for fd in s.scalars(select(FieldDefinition).where(FieldDefinition.placeholder.like("MDM\\_%", escape="\\"))):
            fd.placeholder = NAMING_NEW["placeholder_prefix"] + fd.placeholder[4:]
    s.flush()


def migrate_customers(s: Session) -> None:
    """Older databases have "Kunde" as free text. Turn every name used so far into a customer
    record and store the customer id on the part instead. Runs once; does nothing afterwards."""
    fd = s.scalar(select(FieldDefinition).where(FieldDefinition.entity == "article", FieldDefinition.key == "kunde"))
    if fd is None or fd.type == "customer" or fd.system:
        return
    by_name: dict[str, Customer] = {c.name.lower(): c for c in s.scalars(select(Customer))}

    def customer_for(name: str) -> Customer:
        key = name.strip().lower()
        if key not in by_name:
            c = Customer(name=name.strip())
            s.add(c)
            s.flush()
            by_name[key] = c
        return by_name[key]

    for choice in fd.choices:
        if choice.value.strip():
            customer_for(choice.value)
        choice.active = False
    for article in s.scalars(select(Article)):
        value = (article.data or {}).get("kunde")
        if isinstance(value, str) and value.strip():
            article.data = {**article.data, "kunde": customer_for(value).id}
    fd.type = "customer"
    fd.placeholder = fd.placeholder or (s.get(Setting, "placeholder_prefix").value + "Customer")


DEFAULT_ADMIN_PASSWORD = "admin"  # only valid until the first login sets a new one


def must_change_password(user: User) -> bool:
    """Still the built-in default password: the page asks for a new one before anything else."""
    return user.has_password and verify_password(DEFAULT_ADMIN_PASSWORD, user.password_hash)


def seed(s: Session, config: Config) -> None:
    # New, empty database: built-in admin (admin / admin, or PDB_ADMIN_PASSWORD). The first login
    # must set a new password and runs the setup wizard ("setup_pending").
    if s.scalar(select(User).limit(1)) is None:
        s.add(
            User(
                username=config.initial_admin_user,
                display_name="Administrator",
                password_hash=hash_password(config.initial_admin_password or DEFAULT_ADMIN_PASSWORD),
                role="admin",
            )
        )
        if s.get(Setting, "setup_pending") is None:
            s.add(Setting(key="setup_pending", value="1"))
    for key, value in DEFAULT_SETTINGS.items():
        if s.get(Setting, key) is None:
            s.add(Setting(key=key, value=value))
    if s.get(Counter, "mdm") is None:
        s.add(Counter(name="mdm", value=0))
    _seed_naming(s)
    prefix = s.get(Setting, "placeholder_prefix").value
    _seed_fields(s, "article", ARTICLE_FIELDS, prefix)
    _seed_fields(s, "operation", OPERATION_FIELDS, prefix)
    # the number's old name
    for fd in s.scalars(select(FieldDefinition).where(FieldDefinition.key == "mdm_number", FieldDefinition.label == "MDM-Nr.")):
        fd.label = "PDB-Nr."
    migrate_customers(s)
    s.commit()
