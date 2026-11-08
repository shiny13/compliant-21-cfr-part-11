#!/usr/bin/env bash
# Lint the record-audit-trails chart on its own (no cluster needed).
. "$(dirname "$0")/../../scripts/lib.sh"
need helm
helm dependency update "$(chart_dir record-audit-trails)" >/dev/null
helm lint "$(chart_dir record-audit-trails)" --set signatures.activeSealKeyId=k --set database.password=x
