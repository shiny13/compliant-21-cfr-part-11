#!/usr/bin/env bash
# Lint the authentication-core chart on its own (no cluster needed).
. "$(dirname "$0")/../../scripts/lib.sh"
need helm
helm dependency update "$(chart_dir authentication-core)" >/dev/null
helm lint "$(chart_dir authentication-core)" --set signatures.activeSealKeyId=k --set database.password=x
