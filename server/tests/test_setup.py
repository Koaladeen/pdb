"""First-run setup: built-in admin / admin on an empty database, wizard at its first login."""
import pytest
from fastapi.testclient import TestClient

from app.config import Config
from app.main import create_app


@pytest.fixture
def fresh(tmp_path):
    config = Config(
        database_url=f"sqlite:///{tmp_path / 'fresh.sqlite3'}",
        lock_timeout_minutes=10,
        token_hours=12,
        initial_admin_user="admin",
        initial_admin_password="",  # like a new installation: built-in admin / admin
        data_dir=str(tmp_path / "data"),
    )
    with TestClient(create_app(config)) as c:
        yield c


SETUP = {
    "company": "Muster Feinmechanik",
    "admin": {"username": "chef", "display_name": "Chef Muster", "password": "geheim1"},
    "folders": {"mcam_root": r"\\server\cam\Machines", "nc_root": r"\\server\nc"},
    "fields": "simple",
    "machines": [
        {"name": "DMU 50", "mcam_folder": "DMU50", "nc_folder": "DMU50", "nc_naming": "mdm", "nc_extension": ".H"},
        {"name": "Haas VF2", "mcam_folder": "HaasVF2", "nc_folder": "HaasVF2", "nc_naming": "nc", "nc_extension": ".NC"},
    ],
    "mdm_start": 1000,
}


def first_login(c):
    r = c.post("/auth/login", json={"username": "admin", "password": "admin"})
    assert r.status_code == 200 and r.json()["user"]["must_change_password"] is True
    return {"Authorization": f"Bearer {r.json()['token']}"}


def test_new_installation_runs_the_wizard_once(fresh):
    status = fresh.get("/setup/status").json()
    assert status["needed"] is True and status["company"] == "" and status["suggest"]["mcam_root"] == ""
    assert [u["username"] for u in fresh.get("/auth/users").json()] == ["admin"]  # built-in admin
    assert fresh.post("/setup", json=SETUP).status_code == 401  # only for the logged-in admin
    first = first_login(fresh)

    assert fresh.post("/setup", headers=first, json={**SETUP, "admin": {**SETUP["admin"], "password": "kurz"}}).status_code == 422
    r = fresh.post("/setup", headers=first, json=SETUP)
    assert r.status_code == 201
    h = {"Authorization": f"Bearer {r.json()['token']}"}  # new session; the default-password one is gone
    assert fresh.get("/auth/me", headers=first).status_code == 401
    me = fresh.get("/auth/me", headers=h).json()
    assert me["role"] == "admin" and me["username"] == "chef" and me["must_change_password"] is False
    assert fresh.post("/auth/login", json={"username": "chef", "password": "admin"}).status_code == 401

    assert fresh.get("/setup/status").json() == {"needed": False, "company": "Muster Feinmechanik"}
    assert fresh.post("/setup", headers=h, json=SETUP).status_code == 409  # only once

    settings = fresh.get("/admin/settings", headers=h).json()
    assert settings["mcam_root"] == r"\\server\cam\Machines" and settings["company_name"] == "Muster Feinmechanik"
    machines = {m["name"]: m for m in fresh.get("/admin/machines", headers=h).json()}
    assert machines["Haas VF2"]["nc_naming"] == "nc" and machines["DMU 50"]["nc_extension"] == ".H"

    # "simple" fields: only the main ones switched on, the rest still there to switch on later
    enabled = {f["key"] for f in fresh.get("/fields?entity=article", headers=h).json() if not f["system"]}
    assert enabled == {"teilename", "zeichnungsnummer", "artikelnummer", "kunde", "status", "notiz"}
    assert any(f["key"] == "rohteil_material" and not f["enabled"] for f in fresh.get("/admin/fields?entity=article", headers=h).json())

    # numbering starts after the given value
    part = fresh.post("/articles", headers=h, json={"data": {"teilename": "Erstes Teil"}}).json()
    op = fresh.post(f"/articles/{part['id']}/operations", headers=h, json={"machine_id": machines["DMU 50"]["id"]}).json()
    assert op["mdm_number"] == 1001
    assert op["paths"]["mcam"] == r"\\server\cam\Machines\DMU50\MDM1001\00001001.mcam"


def test_bad_machine_folder_is_refused(fresh):
    first = first_login(fresh)
    bad = {**SETUP, "machines": [{**SETUP["machines"][0], "mcam_folder": "a\\b"}]}
    assert fresh.post("/setup", headers=first, json=bad).status_code == 422
    assert fresh.get("/setup/status").json()["needed"] is True  # nothing half-created
    assert fresh.get("/auth/me", headers=first).status_code == 200  # still logged in


def test_default_password_cannot_be_kept(fresh):
    first = first_login(fresh)
    weak = {**SETUP, "admin": {**SETUP["admin"], "password": "admin"}}
    assert fresh.post("/setup", headers=first, json=weak).status_code == 422
    r = fresh.post("/auth/me/password", headers=first, json={"current_password": "admin", "new_password": "admin"})
    assert r.status_code == 422
    r = fresh.post("/auth/me/password", headers=first, json={"current_password": "admin", "new_password": "neues-pw"})
    assert r.status_code == 200
    assert fresh.get("/auth/me", headers=first).json()["must_change_password"] is False


def test_fields_chosen_one_by_one(fresh):
    first = first_login(fresh)
    body = {**SETUP, "fields": "custom", "enabled_fields": {"article": ["teilename", "rohteil_material"], "operation": ["kommentar"]}}
    h = {"Authorization": f"Bearer {fresh.post('/setup', headers=first, json=body).json()['token']}"}
    on = {(f["entity"], f["key"]) for f in fresh.get("/admin/fields", headers=h).json() if f["enabled"] and not f["system"]}
    assert on == {("article", "teilename"), ("article", "rohteil_material"), ("operation", "kommentar")}
    assert all(f["enabled"] for f in fresh.get("/admin/fields", headers=h).json() if f["system"])
