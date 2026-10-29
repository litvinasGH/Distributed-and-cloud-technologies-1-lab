#!/usr/bin/env sh

DB_NAME="${POSTGRES_DB:-fitness_db}"
DB_USER="${POSTGRES_USER:-fitness_user}"
DB_PASS="${POSTGRES_PASSWORD:-fitness_pass}"

TMP_PRIMARY=$(mktemp /tmp/primary.XXXXXX)
TMP_REPLICA=$(mktemp /tmp/replica.XXXXXX)
trap 'rm -f "$TMP_PRIMARY" "$TMP_REPLICA"' EXIT

echo "Сравниваю db-primary <-> db-replica..."

docker compose exec -T db-primary sh -c "PGPASSWORD='${DB_PASS}' pg_dump -U '${DB_USER}' -d '${DB_NAME}' --data-only --inserts --no-owner --no-privileges" 2>/dev/null \
  | grep -v '^SET ' \
  | grep -v '^--' \
  | grep -v '^[[:space:]]*$' \
  | sort > "$TMP_PRIMARY"

docker compose exec -T db-replica sh -c "PGPASSWORD='${DB_PASS}' pg_dump -U '${DB_USER}' -d '${DB_NAME}' --data-only --inserts --no-owner --no-privileges" 2>/dev/null \
  | grep -v '^SET ' \
  | grep -v '^--' \
  | grep -v '^[[:space:]]*$' \
  | sort > "$TMP_REPLICA"

if cmp -s "$TMP_PRIMARY" "$TMP_REPLICA"; then
  echo "Данные одинаковые"
  exit 0
else
  echo "Данные разные"
  diff -u "$TMP_PRIMARY" "$TMP_REPLICA" | head -40
  exit 1
fi
