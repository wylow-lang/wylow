#!/usr/bin/env bash
# Downloads SQLite's amalgamation from sqlite.org, checks the archive against the SHA3-256 hash that
# sqlite.org's download page lists, and puts sqlite3.c and sqlite3.h in native/. Needs curl and
# python3.
# Usage: bash examples/sqlite/sqlite/fetch-sqlite.sh
set -euo pipefail

name=sqlite-amalgamation-3540100.zip
expected=92281c86e2957f9b9305890f9ac04204caa7b677cc868dc326d942689df28e30

native="$(cd "$(dirname "$0")" && pwd)/native"
download="$(mktemp -d)"
trap 'rm -rf "$download"' EXIT
mkdir -p "$native"

curl -fsSL "https://sqlite.org/2026/$name" -o "$download/$name"
hash="$(python3 -c 'import hashlib, sys; print(hashlib.sha3_256(open(sys.argv[1], "rb").read()).hexdigest())' "$download/$name")"
if [ "$hash" != "$expected" ]; then
    echo "$name has the SHA3-256 hash $hash, not $expected" >&2
    exit 1
fi
python3 -m zipfile -e "$download/$name" "$download/files"
for file in sqlite3.c sqlite3.h; do
    found="$(find "$download/files" -name "$file" | head -n 1)"
    if [ -z "$found" ]; then
        echo "the archive holds no $file" >&2
        exit 1
    fi
    cp "$found" "$native/"
done
echo "SQLite is in $native"
