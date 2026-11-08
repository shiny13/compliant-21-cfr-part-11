#!/usr/bin/env bash
# Run the RecordAuditTrails tests; --coverage also collects line coverage into ./TestResults.
# PostgreSQL tests: set AUDIT_TEST_PG to a connection string that may create databases, e.g.
#   AUDIT_TEST_PG="Host=localhost;Port=...;Username=...;Password=...;Database=postgres" (docker compose up -d first)
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" = "--coverage" ]; then
  dotnet test RecordAuditTrails.slnx --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory ../TestResults/RecordAuditTrails
else
  dotnet test RecordAuditTrails.slnx
fi
