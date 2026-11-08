#!/usr/bin/env bash
# Lint the record-signatures chart on its own (no cluster needed).
. "$(dirname "$0")/../../scripts/lib.sh"
need helm
helm dependency update "$(chart_dir record-signatures)" >/dev/null
helm lint "$(chart_dir record-signatures)" --set signatures.activeSealKeyId=k --set database.password=x
