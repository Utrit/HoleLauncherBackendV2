#!/bin/sh
set -eu

sh /init/configure-drasl.sh

salt=$(openssl rand -hex 16)
password_hex=$(printf '%s' "${ADMIN_INITIAL_PASSWORD:?Missing initial admin password}" | od -An -v -tx1 | tr -d ' \n')
hash=$(openssl kdf -keylen 32 -kdfopt digest:SHA256 -kdfopt "hexpass:$password_hex" -kdfopt "hexsalt:$salt" -kdfopt iter:600000 PBKDF2)
hash=$(printf '%s' "$hash" | tr -d ':[:space:]')
[ "${#hash}" -eq 64 ]
export ADMIN_SALT="$salt" ADMIN_HASH="$hash"
psql --no-psqlrc --no-password --set=ON_ERROR_STOP=1 --single-transaction --file=/init/accounts.sql
psql --no-psqlrc --no-password --set=ON_ERROR_STOP=1 --dbname=postgres --file=/init/create-orchestrator.sql
psql --no-psqlrc --no-password --set=ON_ERROR_STOP=1 --dbname=orchestrator --single-transaction --file=/init/orchestrator.sql
psql --no-psqlrc --no-password --set=ON_ERROR_STOP=1 --dbname=postgres --file=/init/create-game-profiles.sql
exec psql --no-psqlrc --no-password --set=ON_ERROR_STOP=1 --dbname=game_profiles --single-transaction --file=/init/game-profiles.sql
