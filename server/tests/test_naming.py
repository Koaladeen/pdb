"""Naming of operation folders, NC header placeholders and the info file: neutral for new
installations, configurable (e.g. the names of a previous system when taking over its folders
and posts)."""
from sqlalchemy import delete

from app.models import Setting
from app.seed import seed


def test_neutral_names_on_a_new_installation(client, admin, setup):
    s = client.get("/admin/settings", headers=admin).json()
    assert (s["folder_prefix"], s["placeholder_prefix"], s["info_file_extension"]) == ("PDB", "PDB_", "")
    fields = {f["key"]: f for f in client.get("/admin/fields?entity=operation", headers=admin).json()}
    assert fields["mdm_number"]["label"] == "PDB-Nr." and fields["mdm_number"]["placeholder"] == "PDB_FN"


def test_names_of_a_previous_system(client, admin, setup):
    client.put("/admin/settings", headers=admin, json={"folder_prefix": "MDM", "placeholder_prefix": "MDM_", "info_file_extension": ".MDM"})
    assert client.put("/admin/settings", headers=admin, json={"info_file_extension": "MDM"}).status_code == 422  # needs the dot
    h = setup["a"]
    part = client.post("/articles", headers=h, json={"data": {"teilename": "Teil"}}).json()
    op = client.post(f"/articles/{part['id']}/operations", headers=h, json={"machine_id": setup["dmu"]["id"]}).json()
    assert op["paths"]["folder"].endswith(r"DMU50\MDM1") and op["paths"]["nc_info"].endswith("00000001.MDM")
    export = client.post(f"/operations/{op['id']}/nc-export", headers=h).json()
    assert export["placeholder_prefix"] == "MDM_" and export["info_file_text"].startswith("MDM-Information")


def test_existing_data_keeps_its_names(client, admin, setup):
    """A database that already has operations (folders on disk, posts using the placeholders)
    keeps the old names when the naming settings are introduced."""
    h = setup["a"]
    part = client.post("/articles", headers=h, json={"data": {"teilename": "Teil"}}).json()
    client.post(f"/articles/{part['id']}/operations", headers=h, json={"machine_id": setup["dmu"]["id"]})
    db, config = client.app.state.db, client.app.state.config
    with db.sessionmaker() as s:
        s.execute(delete(Setting).where(Setting.key.in_(["folder_prefix", "placeholder_prefix", "info_file_extension"])))
        s.commit()
        seed(s, config)
    got = client.get("/admin/settings", headers=admin).json()
    assert (got["folder_prefix"], got["placeholder_prefix"], got["info_file_extension"]) == ("MDM", "MDM_", ".MDM")
