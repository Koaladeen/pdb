"""Import of existing Mastercam files: grouping into cards, MDM numbers, copying, no duplicates."""
from tests.conftest import login


def test_import_existing_files(client, admin, setup, tmp_path):
    root = tmp_path / "cam"
    client.put("/admin/settings", headers=admin, json={"mcam_root": str(root)})
    old = tmp_path / "alt"
    (old / "Kunde A").mkdir(parents=True)
    f1, f2 = old / "Kunde A" / "4711_OP1.mcam", old / "Kunde A" / "4711_OP2.mcam"
    f1.write_bytes(b"mcam one")
    f2.write_bytes(b"mcam two")
    missing = r"Z:\nicht\erreichbar\0815.mcam"  # e.g. a share only the PCs see

    existing = client.post("/articles", headers=admin, json={"data": {"teilename": "Altes Teil"}}).json()
    body = {
        "cards": [
            {"article": {"teilename": "Halter 4711", "zeichnungsnummer": "4711"},
             "operations": [
                 {"source_path": str(f1), "machine_id": setup["dmu"]["id"], "title": "1 OP Vorderseite"},
                 {"source_path": str(f2), "machine_id": setup["haas"]["id"]},
             ]},
            {"article_id": existing["id"], "operations": [{"source_path": missing, "machine_id": setup["dmu"]["id"]}]},
        ],
    }
    assert client.post("/admin/import/files", headers=setup["a"], json=body).status_code == 403  # admins only
    r = client.post("/admin/import/files", headers=admin, json=body)
    assert r.status_code == 201, r.text
    res = r.json()
    assert (res["imported"], res["copied"], res["pending"], res["skipped"]) == (3, 2, 1, 0)

    new_card = client.get(f"/articles/{res['cards'][0]['article_id']}", headers=admin).json()
    ops = sorted(new_card["operations"], key=lambda o: o["op_number"])
    assert [o["op_number"] for o in ops] == [10, 20]
    assert ops[0]["data"]["prozessbeschreibung"] == "1 OP Vorderseite"
    assert ops[1]["data"]["prozessbeschreibung"] == "4711_OP2"  # file name when no title given
    assert ops[1]["nc_number"] is not None  # FANUC machine: NC number right away

    # copied into the normal operation folder; originals untouched
    op1 = client.get(f"/operations/{ops[0]['id']}", headers=admin).json()
    assert open(op1["paths"]["mcam"], "rb").read() == b"mcam one" and f1.read_bytes() == b"mcam one"
    assert op1["import_source"] is None

    # not reachable by the server: the helper gets the source and reports the copy
    pending = client.get("/admin/import/pending", headers=admin).json()
    assert pending["count"] == 1 and pending["items"][0]["source_path"] == missing
    op3_id = pending["items"][0]["operation_id"]
    assert client.get(f"/operations/{op3_id}", headers=setup["a"]).json()["import_source"] == missing
    assert client.post(f"/operations/{op3_id}/import-copied", headers=setup["a"], json={"pc_name": "PC-A"}).status_code == 200
    assert client.get(f"/operations/{op3_id}", headers=setup["a"]).json()["import_source"] is None
    assert client.get("/admin/import/pending", headers=admin).json()["count"] == 0
    assert any(o["id"] == op3_id for o in client.get(f"/articles/{existing['id']}", headers=admin).json()["operations"])

    # importing the same files again skips them (paths compared without case)
    known = client.post("/admin/import/check", headers=admin, json={"paths": [str(f1).upper(), str(old / "neu.mcam")]}).json()["known"]
    assert list(known) == [str(f1).upper()]
    again = client.post("/admin/import/files", headers=admin, json={"cards": [body["cards"][0]]}).json()
    assert again["imported"] == 0 and again["skipped"] == 2


def test_import_needs_required_card_fields(client, admin, setup):
    body = {"cards": [{"article": {}, "operations": [{"source_path": r"C:\x\a.mcam", "machine_id": setup["dmu"]["id"]}]}]}
    assert client.post("/admin/import/files", headers=admin, json=body).status_code == 422
    assert client.get("/admin/import/pending", headers=admin).json()["count"] == 0  # nothing half-created
