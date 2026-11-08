#!/usr/bin/env bash
# Run the AuthenticationCore tests; --coverage also collects line coverage into ./TestResults.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" = "--coverage" ]; then
  dotnet test AuthenticationCore.sln  --collect:"XPlat Code Coverage" --results-directory ../TestResults/AuthenticationCore
else
  dotnet test AuthenticationCore.sln
fi
