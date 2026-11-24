from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta

from sqlalchemy import update

from app.models import Lock, utcnow

from .conftest import login, make_customer, make_op, make_part

# ---------------------------------------------------------------- auth and rights


def test_login_and_me(client, admin):
    assert client.post("/auth/login", json={"username": "admin", "password": "nope"}).status_code == 401
    assert client.get("/auth/me").status_code == 401
    me = client.get("/auth/me", headers=admin).json()
    assert me["role"] == "admin"


def test_admin_only(client, setup):
    assert client.get("/admin/users", headers=setup["a"]).status_code == 403
    assert client.post("/admin/machines", headers=setup["a"], json={}).status_code in (403, 422)


def test_machine_right_required(client, admin, setup):
    me = client.get("/auth/me", headers=setup["a"]).json()
    client.put(f"/admin/users/{me['id']}/machines", headers=admin, json={"machine_ids": [setup["dmu"]["id"]]})
    part = make_part(client, setup["a"])
    r = client.post(f"/articles/{part['id']}/operations", headers=setup["a"], json={"machine_id": setup["haas"]["id"]})
    assert r.status_code == 403


def test_last_admin_protected(client, admin):
    me = client.get("/auth/me", headers=admin).json()
    assert client.patch(f"/admin/users/{me['id']}", headers=admin, json={"role": "user"}).status_code == 409


def test_login_without_password(client, admin):
    r = client.post("/admin/users", headers=admin, json={"username": "lina", "display_name": "Lina Muster"})
    assert r.status_code == 201 and r.json()["has_password"] is False

    # login list is public and shows who needs a password
    users = {u["username"]: u for u in client.get("/auth/users").json()}
    assert users["lina"]["has_password"] is False and users["admin"]["has_password"] is True
    assert "role" not in users["lina"]

    assert client.post("/auth/login", json={"username": "lina"}).status_code == 200
    assert client.post("/auth/login", json={"username": "admin"}).status_code == 401

    # admins always need a password
    assert client.post("/admin/users", headers=admin, json={"username": "boss", "display_name": "B", "role": "admin"}).status_code == 409
    assert client.patch(f"/admin/users/{r.json()['id']}", headers=admin, json={"role": "admin"}).status_code == 409
    me = client.get("/auth/me", headers=admin).json()
    assert client.patch(f"/admin/users/{me['id']}", headers=admin, json={"remove_password": True}).status_code == 409
    assert client.post("/auth/me/password", headers=admin, json={"current_password": "admin"}).status_code == 409

    # a user sets a password himself, then needs it, then removes it again
    lina = login(client, "lina", "")
    assert client.post("/auth/me/password", headers=lina, json={"new_password": "geheim"}).json()["has_password"] is True
    assert client.post("/auth/login", json={"username": "lina"}).status_code == 401
    assert client.post("/auth/me/password", headers=lina, json={"current_password": "falsch"}).status_code == 403
    assert client.post("/auth/me/password", headers=lina, json={"current_password": "geheim"}).json()["has_password"] is False


def test_preferences_follow_the_user(client, admin, setup):
    a = setup["a"]
    defaults = client.get("/auth/me/preferences", headers=a).json()
    assert defaults["theme"] == "auto" and defaults["size"] == "m" and defaults["sidebar"] is True

    mine = {**defaults, "theme": "dark", "accent": "#1F7A6B", "size": "l", "density": "compact", "sidebar": False}
    assert client.put("/auth/me/preferences", headers=a, json={**mine, "junk": 1}).json() == mine
    # same user on another PC (new login) gets them too, other users not
    again = login(client, "anna", "secret", "PC-OTHER")
    assert client.get("/auth/me", headers=again).json()["preferences"] == mine
    assert client.get("/auth/me/preferences", headers=setup["b"]).json() == defaults

    assert client.put("/auth/me/preferences", headers=a, json={**mine, "accent": "red"}).status_code == 422
    assert client.put("/auth/me/preferences", headers=a, json={**mine, "size": "xxl"}).status_code == 422


def test_workspace_is_per_user_and_follows_the_user(client, setup):
    a, b = setup["a"], setup["b"]
    assert client.get("/auth/me", headers=a).json()["workspace"] == {"cards": [], "recent": []}
    ws = {"cards": [{"id": 5, "title": "Flat Grid", "subtitle": "NR-20431 · Nordic"}], "recent": [{"filters": {"q": "grid"}, "at": 1}]}
    assert client.put("/auth/me/workspace", headers=a, json=ws).json() == ws
    other_pc = login(client, "anna", "secret", "PC-OTHER")
    assert client.get("/auth/me", headers=other_pc).json()["workspace"] == ws  # same user, other PC
    assert client.get("/auth/me/workspace", headers=b).json() == {"cards": [], "recent": []}  # other user
    too_many = {"cards": [{"id": i} for i in range(60)], "recent": []}
    assert client.put("/auth/me/workspace", headers=a, json=too_many).status_code == 422


def test_avatars(client, admin):
    png = b"\x89PNG\r\n\x1a\n" + b"\x01" * 16
    uid = client.post("/admin/users", headers=admin, json={"username": "lina", "display_name": "Lina"}).json()["id"]
    lina = login(client, "lina", "")
    assert client.get(f"/users/{uid}/avatar").status_code == 404
    assert client.put("/auth/me/avatar", headers=lina, content=b"nope").status_code == 415
    assert client.put("/auth/me/avatar", headers=lina, content=png).json()["avatar_at"]
    assert client.get(f"/users/{uid}/avatar").content == png  # public, for the login page
    assert next(u for u in client.get("/auth/users").json() if u["id"] == uid)["avatar_at"]
    assert client.put(f"/admin/users/{uid}/avatar", headers=lina, content=png).status_code == 403
    assert client.delete(f"/admin/users/{uid}/avatar", headers=admin).json()["avatar_at"] is None


# ---------------------------------------------------------------- parts, operations, numbers, paths


def test_operation_numbers_and_paths(client, setup):
    part = make_part(client, setup["a"], zeichnungsnummer="NR-20431", kunde=make_customer(client, setup["a"], "Nordic"))
    op1 = make_op(client, setup["a"], part["id"], setup["dmu"]["id"])
    op2 = make_op(client, setup["a"], part["id"], setup["haas"]["id"])

    assert (op1["mdm_number"], op1["op_number"], op1["nc_number"]) == (1, 10, None)
    # FANUC-style machine gets its NC number straight away
    assert (op2["mdm_number"], op2["op_number"], op2["nc_number"]) == (2, 20, 1)

    p = op1["paths"]
    assert p["folder"] == r"M:\CAM\Machines\DMU50\MDM1"
    assert p["mcam"] == r"M:\CAM\Machines\DMU50\MDM1\00000001.mcam"
    assert p["nc_export"] == r"H:\NC\DMU50_NC\00000001.H"
    assert p["nc_info"] == r"H:\NC\DMU50_NC\00000001.MDM"
    assert op2["paths"]["nc_export"] == r"H:\NC\HAAS_VF2\1.NC"

    card = client.get(f"/articles/{part['id']}", headers=setup["a"]).json()
    assert [o["mdm_number"] for o in card["operations"]] == [1, 2]


def test_admin_sets_counter_start(client, admin, setup):
    client.put("/admin/counters/mdm", headers=admin, json={"value": 84196})
    client.put(f"/admin/counters/nc:{setup['haas']['id']}", headers=admin, json={"value": 1100})
    part = make_part(client, setup["a"])
    op = make_op(client, setup["a"], part["id"], setup["haas"]["id"])
    assert op["mdm_number"] == 84197
    assert op["nc_number"] == 1101
    assert op["paths"]["nc_export"].endswith(r"HAAS_VF2\1101.NC")


def test_nc_numbers_count_per_machine(client, setup):
    part = make_part(client, setup["a"])
    a = make_op(client, setup["a"], part["id"], setup["dmu"]["id"], assign_nc_number=True)
    b = make_op(client, setup["a"], part["id"], setup["haas"]["id"])
    c = make_op(client, setup["a"], part["id"], setup["dmu"]["id"])
    assert a["nc_number"] == 1 and b["nc_number"] == 1
    c = client.post(f"/operations/{c['id']}/nc-number", headers=setup["a"]).json()
    assert c["nc_number"] == 2


def test_parallel_numbers_are_unique(client, setup):
    part = make_part(client, setup["a"])

    def create(_):
        return make_op(client, setup["a"], part["id"], setup["haas"]["id"])

    with ThreadPoolExecutor(8) as pool:
        ops = list(pool.map(create, range(16)))
    assert len({o["mdm_number"] for o in ops}) == 16
    assert len({o["nc_number"] for o in ops}) == 16


def test_machine_change(client, setup):
    part = make_part(client, setup["a"])
    op = make_op(client, setup["a"], part["id"], setup["dmu"]["id"])
    r = client.post(f"/operations/{op['id']}/machine", headers=setup["a"], json={"machine_id": setup["haas"]["id"]})
    assert r.status_code == 200, r.text
    moved = r.json()
    assert moved["machine"] == "Haas VF-2"
    assert moved["nc_number"] == 1
    assert moved["old_paths"]["folder"].endswith(r"DMU50\MDM1")
    assert moved["paths"]["folder"].endswith(r"Haas_VF2\MDM1")


# ---------------------------------------------------------------- configurable fields


def test_field_validation(client, setup):
    h = setup["a"]
    assert client.post("/articles", headers=h, json={"data": {}}).status_code == 422  # Teilename required
    assert client.post("/articles", headers=h, json={"data": {"teilename": "x", "nope": 1}}).status_code == 422
    assert client.post("/articles", headers=h, json={"data": {"teilename": "x", "stueckzahl": "zwei"}}).status_code == 422
    assert client.post("/articles", headers=h, json={"data": {"teilename": "x", "status": "Vielleicht"}}).status_code == 422
    assert client.post("/articles", headers=h, json={"data": {"teilename": "x", "startdatum_auftrag": "05.10.2026"}}).status_code == 422
    ok = client.post(
        "/articles",
        headers=h,
        json={"data": {"teilename": "x", "stueckzahl": 24, "status": "Freigegeben", "startdatum_auftrag": "2026-10-05"}},
    )
    assert ok.status_code == 201, ok.text


def test_disable_and_add_fields(client, admin, setup):
    fields = client.get("/admin/fields?entity=article", headers=admin).json()
    profile = next(f for f in fields if f["key"] == "profile")
    part = make_part(client, setup["a"], profile="L-Profil")

    client.patch(f"/admin/fields/{profile['id']}", headers=admin, json={"enabled": False})
    assert "profile" not in [f["key"] for f in client.get("/fields?entity=article", headers=setup["a"]).json()]
    r = client.patch(f"/articles/{part['id']}", headers=setup["a"], json={"data": {"profile": "neu"}})
    assert r.status_code == 422
    # value kept while disabled
    assert client.get(f"/articles/{part['id']}", headers=setup["a"]).json()["data"]["profile"] == "L-Profil"

    mdm = next(f for f in client.get("/admin/fields?entity=operation", headers=admin).json() if f["key"] == "mdm_number")
    assert client.patch(f"/admin/fields/{mdm['id']}", headers=admin, json={"enabled": False}).status_code == 409
    assert client.delete(f"/admin/fields/{mdm['id']}", headers=admin).status_code == 409

    r = client.post(
        "/admin/fields",
        headers=admin,
        json={"entity": "article", "key": "oberflaeche", "label": "Oberfläche", "type": "suggest", "searchable": True},
    )
    assert r.status_code == 201, r.text
    client.patch(f"/articles/{part['id']}", headers=setup["a"], json={"data": {"oberflaeche": "eloxiert"}})
    found = client.get("/articles?q=eloxiert", headers=setup["a"]).json()
    assert found["total"] == 1


def test_search(client, setup):
    h = setup["a"]
    nordic = make_customer(client, h, "Nordic")
    other = make_customer(client, h, "Other")
    p1 = make_part(client, h, teilename="Ring Bracket R18-A", kunde=nordic, bestellnummer="400012345")
    make_part(client, h, teilename="Flat Grid", kunde=other)
    me = client.get("/auth/me", headers=h).json()
    op = make_op(client, h, p1["id"], setup["dmu"]["id"], data={"prozessbeschreibung": "1 OP Wandstaerke", "cam_programmierer": me["id"]})
    assert client.get("/articles?q=bracket", headers=h).json()["total"] == 1
    assert client.get(f"/articles?q={op['mdm_number']}", headers=h).json()["items"][0]["id"] == p1["id"]
    assert client.get(f"/articles?article.kunde={nordic}", headers=h).json()["total"] == 1
    assert client.get("/articles?q=nord", headers=h).json()["total"] == 1  # customer by name
    assert client.get("/articles?q=anna", headers=h).json()["total"] == 1  # programmer by name
    assert client.get("/articles?q=wandstaerke", headers=h).json()["total"] == 1
    assert client.get("/articles?article.bestellnummer=40001", headers=h).json()["total"] == 1
    assert client.get(f"/articles?machine_id={setup['dmu']['id']}", headers=h).json()["total"] == 1
    assert client.get("/articles?article.unknown=x", headers=h).status_code == 422
    assert client.get("/articles", headers=h).json()["total"] == 2
    assert "400012345" in client.get("/suggest/article/bestellnummer?q=4000", headers=h).json()


# ---------------------------------------------------------------- customers


def test_customers(client, admin, setup):
    h = setup["a"]
    cid = make_customer(client, h, "Nordic", number="K-100", contact="Frau Muster")
    assert client.post("/customers", headers=h, json={"name": "nordic"}).status_code == 409  # same name, other case
    assert client.post("/articles", headers=h, json={"data": {"teilename": "x", "kunde": 9999}}).status_code == 422
    assert client.post("/articles", headers=h, json={"data": {"teilename": "x", "kunde": "Nordic"}}).status_code == 422
    part = make_part(client, h, kunde=cid)

    # renaming the customer shows up on every part, because parts store the id
    client.patch(f"/customers/{cid}", headers=h, json={"name": "Nordic AG"})
    listed = client.get("/customers", headers=h).json()
    assert listed == [{**listed[0], "name": "Nordic AG", "number": "K-100", "parts": 1}]

    # users may not delete or deactivate, admins deactivate a used customer
    assert client.delete(f"/customers/{cid}", headers=h).status_code == 403
    assert client.patch(f"/customers/{cid}", headers=h, json={"active": False}).status_code == 403
    assert client.delete(f"/customers/{cid}", headers=admin).json() == {"result": "deactivated"}
    assert client.get("/customers", headers=h).json() == []
    assert len(client.get("/customers?include_inactive=true", headers=h).json()) == 1
    assert client.get(f"/articles/{part['id']}", headers=h).json()["data"]["kunde"] == cid  # part keeps it

    unused = make_customer(client, h, "Einmalig")
    assert client.delete(f"/customers/{unused}", headers=admin).json() == {"result": "deleted"}


def test_free_text_customers_are_migrated(client, admin, setup):
    """Old databases stored the customer name as text; on start they become customer records."""
    from app.models import Article, FieldDefinition
    from app.seed import migrate_customers

    with client.app.state.db.sessionmaker() as s:
        fd = s.query(FieldDefinition).filter_by(entity="article", key="kunde").one()
        fd.type = "suggest"
        s.add_all([Article(data={"teilename": "A", "kunde": "Nordic"}), Article(data={"teilename": "B", "kunde": "nordic "}), Article(data={"teilename": "C"})])
        s.commit()
        migrate_customers(s)
        s.commit()
        kunden = {a.data["teilename"]: a.data.get("kunde") for a in s.query(Article)}
        assert s.query(FieldDefinition).filter_by(entity="article", key="kunde").one().type == "customer"
    customers = client.get("/customers", headers=admin).json()
    assert [c["name"] for c in customers] == ["Nordic"]
    assert kunden == {"A": customers[0]["id"], "B": customers[0]["id"], "C": None}


# ---------------------------------------------------------------- locks and revisions


def test_lock_cycle(client, setup):
    a, b = setup["a"], setup["b"]
    part = make_part(client, a)
    op = make_op(client, a, part["id"], setup["dmu"]["id"])
    url = f"/operations/{op['id']}"

    r = client.post(f"{url}/checkout", headers=a, json={"pc_name": "PC-A"})
    assert r.status_code == 200, r.text
    assert r.json()["paths"]["next_archive"].endswith(r"MDM1\Revisionen\00000001_r0001.mcam")

    # B is refused and told who holds it -> read-only
    r = client.post(f"{url}/checkout", headers=b, json={"pc_name": "PC-B"})
    assert r.status_code == 409
    assert r.json()["detail"]["lock"]["user"] == "Anna Muster"
    assert r.json()["detail"]["lock"]["pc_name"] == "PC-A"

    # same user, other PC is also refused
    assert client.post(f"{url}/checkout", headers=a, json={"pc_name": "PC-X"}).status_code == 409
    # same user, same PC refreshes
    assert client.post(f"{url}/checkout", headers=a, json={"pc_name": "PC-A"}).status_code == 200

    assert client.post(f"{url}/heartbeat", headers=a, json={"pc_name": "PC-A"}).status_code == 200
    assert client.post(f"{url}/heartbeat", headers=b, json={"pc_name": "PC-B"}).status_code == 409

    # wrong revision number is refused
    assert client.post(f"{url}/checkin", headers=a, json={"pc_name": "PC-A", "revision": 5}).status_code == 409
    r = client.post(f"{url}/checkin", headers=a, json={"pc_name": "PC-A", "revision": 1, "comment": "first"})
    assert r.status_code == 200, r.text
    assert r.json()["archive_path"].endswith(r"Revisionen\00000001_r0001.mcam")

    # lock released by check-in: B can now take it
    assert client.post(f"{url}/checkout", headers=b, json={"pc_name": "PC-B"}).status_code == 200
    r = client.post(f"{url}/checkin", headers=b, json={"pc_name": "PC-B", "revision": 2, "keep_lock": True})
    assert r.json()["locked"] is True
    assert client.post(f"{url}/release", headers=b, json={"pc_name": "PC-B"}).status_code == 200
    assert client.get("/locks", headers=a).json() == []

    revs = client.get(f"{url}/revisions", headers=a).json()
    assert [(r["number"], r["saved_by"]) for r in revs] == [(2, "Tom Muster"), (1, "Anna Muster")]


def test_stale_lock_takeover(client, setup):
    a, b = setup["a"], setup["b"]
    part = make_part(client, a)
    op = make_op(client, a, part["id"], setup["dmu"]["id"])
    url = f"/operations/{op['id']}"
    client.post(f"{url}/checkout", headers=a, json={"pc_name": "PC-A"})

    # simulate a crashed PC: last heartbeat 11 minutes ago
    with client.app.state.db.sessionmaker() as s:
        s.execute(update(Lock).values(last_heartbeat=utcnow() - timedelta(minutes=11)))
        s.commit()
    assert client.get("/locks", headers=a).json()[0]["stale"] is True

    assert client.post(f"{url}/checkout", headers=b, json={"pc_name": "PC-B"}).status_code == 200
    # the crashed client finds out it lost the lock
    assert client.post(f"{url}/heartbeat", headers=a, json={"pc_name": "PC-A"}).status_code == 409


def test_break_lock(client, admin, setup):
    part = make_part(client, setup["a"])
    op = make_op(client, setup["a"], part["id"], setup["dmu"]["id"])
    client.post(f"/operations/{op['id']}/checkout", headers=setup["a"], json={"pc_name": "PC-A"})
    assert client.post(f"/admin/break-lock/{op['id']}", headers=setup["b"]).status_code == 403
    assert client.post(f"/admin/break-lock/{op['id']}", headers=admin).status_code == 200
    assert client.post(f"/operations/{op['id']}/checkout", headers=setup["b"], json={"pc_name": "PC-B"}).status_code == 200
    actions = [e["action"] for e in client.get(f"/admin/audit?operation_id={op['id']}", headers=admin).json()]
    assert "break_lock" in actions


def test_parallel_checkout_one_winner(client, setup):
    part = make_part(client, setup["a"])
    op = make_op(client, setup["a"], part["id"], setup["dmu"]["id"])
    users = [login(client, "anna" if i % 2 else "tom", "secret", f"PC-{i}") for i in range(10)]

    def try_lock(i):
        return client.post(f"/operations/{op['id']}/checkout", headers=users[i], json={"pc_name": f"PC-{i}"}).status_code

    with ThreadPoolExecutor(10) as pool:
        codes = list(pool.map(try_lock, range(10)))
    assert codes.count(200) == 1
    assert codes.count(409) == 9


# ---------------------------------------------------------------- downloads (helper, bridge)


def test_downloads(client, tmp_path):
    assert client.get("/downloads/version.json").json() == {"helper": None, "bridge": {}, "shared_addins": {}}
    assert client.get("/downloads/PdbHelper.exe").status_code == 404
    folder = tmp_path / "data" / "downloads"
    (folder / "PdbHelper-0.2.0.exe").write_bytes(b"MZ-helper")
    (folder / "version.json").write_text('{"helper": {"version": "0.2.0", "file": "PdbHelper-0.2.0.exe"}}', encoding="utf-8")
    assert client.get("/downloads/version.json").json()["helper"]["version"] == "0.2.0"
    assert client.get("/downloads/PdbHelper-0.2.0.exe").content == b"MZ-helper"  # no login needed
    assert client.get("/downloads/..%2Fversion.json").status_code == 404
    assert client.get("/downloads/.hidden").status_code == 404


# ---------------------------------------------------------------- reading existing folders


def test_import_operation(client, admin, setup):
    url = "/admin/import/operation"
    body = {
        "machine_id": setup["dmu"]["id"],
        "mdm_number": 91704,
        "article": {"teilename": "NUT TIGHTENER", "zeichnungsnummer": "P2029244-000"},
        "operation": {"prozessbeschreibung": "1 SPANNUNG"},
    }
    assert client.post(url, headers=setup["a"], json=body).status_code == 403  # admin only

    dry = client.post(url, headers=admin, json={**body, "dry_run": True}).json()
    assert dry == {"result": "would create", "article_created": True, "op_number": 10}
    assert client.get("/articles", headers=admin).json()["total"] == 0  # nothing written

    first = client.post(url, headers=admin, json=body).json()
    assert first["result"] == "created" and first["article_created"]
    # second operation of the same part (name case differs) joins the same part
    second = client.post(url, headers=admin, json={**body, "mdm_number": 91695, "article": {"teilename": "nut tightener", "zeichnungsnummer": "p2029244-000"}}).json()
    assert second["article_id"] == first["article_id"] and not second["article_created"] and second["op_number"] == 20
    # repeating is harmless
    assert client.post(url, headers=admin, json=body).json()["result"] == "exists"

    card = client.get(f"/articles/{first['article_id']}", headers=admin).json()
    assert [o["mdm_number"] for o in card["operations"]] == [91704, 91695]
    paths = client.get(f"/operations/{first['operation_id']}", headers=admin).json()["paths"]
    assert paths["mcam"].endswith(r"DMU50\MDM91704\00091704.mcam")  # the existing folder
    # new numbers from the counter skip nothing they shouldn't and never reuse an imported one
    new = make_op(client, admin, first["article_id"], setup["dmu"]["id"])
    assert new["mdm_number"] not in (91704, 91695)


# ---------------------------------------------------------------- pictures

PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 32


def test_operation_picture(client, admin, setup):
    a, b = setup["a"], setup["b"]
    part = make_part(client, a)
    op = make_op(client, a, part["id"], setup["dmu"]["id"])
    url = f"/operations/{op['id']}/picture"
    assert op["picture_at"] is None
    assert client.get(url, headers=a).status_code == 404

    assert client.put(url, headers=a, content=b"not an image").status_code == 415
    r = client.put(url, headers=a, content=PNG)
    assert r.status_code == 200 and r.json()["picture_at"]
    got = client.get(url, headers=b)
    assert got.content == PNG and got.headers["content-type"] == "image/png"
    assert client.get(f"/operations/{op['id']}", headers=a).json()["picture_at"] == r.json()["picture_at"]
    assert client.get(f"/articles/{part['id']}", headers=a).json()["operations"][0]["picture_at"]

    # no machine right -> no upload
    me = client.get("/auth/me", headers=b).json()
    client.put(f"/admin/users/{me['id']}/machines", headers=admin, json={"machine_ids": []})
    assert client.put(url, headers=b, content=PNG).status_code == 403

    assert client.delete(url, headers=a).status_code == 200
    assert client.get(url, headers=a).status_code == 404


# ---------------------------------------------------------------- NC export


def test_nc_export(client, setup):
    a = setup["a"]
    me = client.get("/auth/me", headers=a).json()
    part = make_part(client, a, teilename="Flat Grid", zeichnungsnummer="NR-20431")
    op = make_op(
        client,
        a,
        part["id"],
        setup["dmu"]["id"],
        data={"prozessbeschreibung": "1 OP", "cam_programmierer": me["id"], "operations_status": "Gesperrt"},
    )
    url = f"/operations/{op['id']}/nc-export"
    assert client.post(url, headers=a).status_code == 409  # locked for production

    client.patch(f"/operations/{op['id']}", headers=a, json={"data": {"operations_status": "Freigegeben"}})
    r = client.post(url, headers=a)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["header_values"] == {
        "#MDM_Partname": "Flat Grid",
        "#MDM_DrawingNumber": "NR-20431",
        "#MDM_FN": "00000001",
        "#MDM_OperationTitle": "1 OP",
        "#MDM_Programmer": "Anna Muster",
        "#MDM_Customer": "",
    }
    assert body["program_name"] == "00000001"
    assert "Description : NR-20431 \u2666 Flat Grid" in body["info_file_text"]
    assert r"DNC-Location: H:\NC\DMU50_NC\00000001.H" in body["info_file_text"]

    # part status locked also blocks
    client.patch(f"/articles/{part['id']}", headers=a, json={"data": {"status": "Gesperrt"}})
    assert client.post(url, headers=a).status_code == 409
