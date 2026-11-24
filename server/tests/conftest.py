import pytest
from fastapi.testclient import TestClient

from app.config import Config
from app.main import create_app


@pytest.fixture
def client(tmp_path):
    config = Config(
        database_url=f"sqlite:///{tmp_path / 'test.sqlite3'}",
        lock_timeout_minutes=10,
        token_hours=12,
        initial_admin_user="admin",
        initial_admin_password="admin",
        data_dir=str(tmp_path / "data"),
    )
    app = create_app(config)
    with TestClient(app) as c:
        yield c


def login(client, username, password, pc="PC-TEST"):
    r = client.post("/auth/login", json={"username": username, "password": password, "pc_name": pc})
    assert r.status_code == 200, r.text
    return {"Authorization": f"Bearer {r.json()['token']}"}


@pytest.fixture
def admin(client):
    return login(client, "admin", "admin", "PC-ADMIN")


@pytest.fixture
def setup(client, admin):
    """Two machines like on the share (Heidenhain style and FANUC style), two users with rights."""
    # main folders as an admin (or the setup wizard) sets them - a new installation has none
    client.put("/admin/settings", headers=admin, json={"mcam_root": r"M:\CAM\Machines", "nc_root": r"H:\NC"})
    dmu = client.post(
        "/admin/machines",
        headers=admin,
        json={
            "name": "DMU 50",
            "mcam_folder": "DMU50",
            "nc_folder": "DMU50_NC",
            "nc_naming": "mdm",
            "nc_extension": ".H",
        },
    ).json()
    haas = client.post(
        "/admin/machines",
        headers=admin,
        json={
            "name": "Haas VF-2",
            "mcam_folder": "Haas_VF2",
            "nc_folder": "HAAS_VF2",
            "nc_naming": "nc",
            "nc_extension": ".NC",
            "nc_number_max": 9999,
        },
    ).json()
    for name in ("anna", "tom"):
        r = client.post(
            "/admin/users",
            headers=admin,
            json={
                "username": name,
                "display_name": name.title() + " Muster",
                "password": "secret",
                "machine_ids": [dmu["id"], haas["id"]],
            },
        )
        assert r.status_code == 201, r.text
    return {
        "dmu": dmu,
        "haas": haas,
        "a": login(client, "anna", "secret", "PC-A"),
        "b": login(client, "tom", "secret", "PC-B"),
    }


def make_customer(client, headers, name, **extra):
    r = client.post("/customers", headers=headers, json={"name": name, **extra})
    assert r.status_code == 201, r.text
    return r.json()["id"]


def make_part(client, headers, **data):
    data.setdefault("teilename", "Flat Grid")
    r = client.post("/articles", headers=headers, json={"data": data})
    assert r.status_code == 201, r.text
    return r.json()


def make_op(client, headers, article_id, machine_id, **kw):
    r = client.post(f"/articles/{article_id}/operations", headers=headers, json={"machine_id": machine_id, **kw})
    assert r.status_code == 201, r.text
    return r.json()
