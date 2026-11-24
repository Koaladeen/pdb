#!/usr/bin/env bash
# Smoke test against a running server with curl: two users, one lock.
# Usage: scripts/smoke.sh [http://localhost:8000]   (needs a fresh database, admin/admin)
set -euo pipefail
BASE="${1:-http://localhost:8000}"

j() { python -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1], {}, {'d': d}))" "$1"; }
post() { curl -s -X POST "$BASE$1" -H "Authorization: Bearer $2" -H 'Content-Type: application/json' -d "$3"; }
code() { curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE$1" -H "Authorization: Bearer $2" -H 'Content-Type: application/json' -d "$3"; }

ADMIN=$(curl -s -X POST "$BASE/auth/login" -H 'Content-Type: application/json' -d '{"username":"admin","password":"admin","pc_name":"SMOKE"}' | j "d['token']")
M=$(post /admin/machines "$ADMIN" '{"name":"Smoke Dmu","mcam_folder":"Smoke_Dmu","nc_folder":"SMOKE_DMU","nc_extension":".H"}' | j "d['id']")
post /admin/users "$ADMIN" "{\"username\":\"smoke_a\",\"display_name\":\"Smoke A\",\"password\":\"secret\",\"machine_ids\":[$M]}" >/dev/null
post /admin/users "$ADMIN" "{\"username\":\"smoke_b\",\"display_name\":\"Smoke B\",\"password\":\"secret\",\"machine_ids\":[$M]}" >/dev/null
A=$(curl -s -X POST "$BASE/auth/login" -H 'Content-Type: application/json' -d '{"username":"smoke_a","password":"secret","pc_name":"PC-A"}' | j "d['token']")
B=$(curl -s -X POST "$BASE/auth/login" -H 'Content-Type: application/json' -d '{"username":"smoke_b","password":"secret","pc_name":"PC-B"}' | j "d['token']")

PART=$(post /articles "$A" '{"data":{"teilename":"Smoke Part","zeichnungsnummer":"SM-1"}}' | j "d['id']")
OP=$(post "/articles/$PART/operations" "$A" "{\"machine_id\":$M}")
OPID=$(echo "$OP" | j "d['id']")
echo "operation:   MDM$(echo "$OP" | j "d['mdm_number_text']") -> $(echo "$OP" | j "d['paths']['mcam']")"

echo "A checkout:  $(code "/operations/$OPID/checkout" "$A" '{"pc_name":"PC-A"}')   (expect 200)"
echo "B checkout:  $(code "/operations/$OPID/checkout" "$B" '{"pc_name":"PC-B"}')   (expect 409, read-only)"
echo "A checkin:   $(code "/operations/$OPID/checkin" "$A" '{"pc_name":"PC-A","revision":1,"comment":"smoke"}')   (expect 200)"
echo "B checkout:  $(code "/operations/$OPID/checkout" "$B" '{"pc_name":"PC-B"}')   (expect 200)"
echo "B release:   $(code "/operations/$OPID/release" "$B" '{"pc_name":"PC-B"}')   (expect 200)"
echo "NC export:   $(post "/operations/$OPID/nc-export" "$A" '{}' | j "d['paths']['nc_export']")"
