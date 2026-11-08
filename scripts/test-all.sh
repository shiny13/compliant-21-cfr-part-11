#!/usr/bin/env bash
# Run every test suite. With --coverage, also collect line coverage (results in ./TestResults/<project>).
# PostgreSQL tests run when the *_TEST_PG variables are set (see each project's README); otherwise they are skipped.
#
#   scripts/test-all.sh
#   AUDIT_TEST_PG=... SIGNATURES_TEST_PG=... scripts/test-all.sh --coverage
. "$(dirname "$0")/lib.sh"
need dotnet

COV=0; [ "${1:-}" = "--coverage" ] && COV=1
for svc in "${SERVICES[@]}"; do
  "$REPO_ROOT/${SERVICE_DIR[$svc]}/scripts/test.sh" $([ "$COV" = 1 ] && echo --coverage)
done
