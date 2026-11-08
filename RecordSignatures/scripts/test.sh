#!/usr/bin/env bash
# Run the RecordSignatures tests; --coverage also collects line coverage into ./TestResults.
# PostgreSQL tests: set SIGNATURES_TEST_PG to a connection string that may create databases, e.g.
#   SIGNATURES_TEST_PG="Host=localhost;Port=...;Username=...;Password=...;Database=postgres" (docker compose up -d first)
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" = "--coverage" ]; then
  dotnet test RecordSignatures.slnx --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory ../TestResults/RecordSignatures
else
  dotnet test RecordSignatures.slnx
fi
