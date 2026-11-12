#!/usr/bin/env sh
# Apply migrations to the dev database, then regenerate the SqlHydra types from it.
# Reads hub.database from ./harbor.json (repo root).
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
cs=$(python3 -c 'import json,re,sys; s=open("harbor.json").read(); s=re.sub(r"^\s*//.*$","",s,flags=re.M); print(json.loads(s)["hub"]["database"])')
gen=src/Harbor.Db/HarborDb.fs
[ -f "$gen" ] || echo 'namespace Harbor.HarborDb' > "$gen"  # first run: let Harbor build so it can migrate
dotnet run --project src/Harbor -- migrate
cd src/Harbor.Db && dotnet sqlhydra npgsql -cs "$cs"
