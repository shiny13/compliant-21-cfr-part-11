#!/bin/sh
# Creates one database and one least-privilege login role per service.
# Runs once, on first start of an empty PostgreSQL data directory (docker-entrypoint-initdb.d).
# Required environment: AUDIT_DB_PASSWORD, SIGNATURES_DB_PASSWORD (and the usual POSTGRES_USER / POSTGRES_PASSWORD).
set -eu

: "${AUDIT_DB_PASSWORD:?AUDIT_DB_PASSWORD is required}"
: "${SIGNATURES_DB_PASSWORD:?SIGNATURES_DB_PASSWORD is required}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v audit_pw="$AUDIT_DB_PASSWORD" -v sig_pw="$SIGNATURES_DB_PASSWORD" <<'SQL'
CREATE ROLE audit_app LOGIN PASSWORD :'audit_pw';
CREATE DATABASE record_audit_trails OWNER audit_app;
REVOKE ALL ON DATABASE record_audit_trails FROM PUBLIC;

CREATE ROLE signatures_app LOGIN PASSWORD :'sig_pw';
CREATE DATABASE record_signatures OWNER signatures_app;
REVOKE ALL ON DATABASE record_signatures FROM PUBLIC;
SQL
