#!/usr/bin/env bash
# Lint every chart and render each with development values to catch template errors. No cluster needed.
. "$(dirname "$0")/lib.sh"
need helm

log "resolving chart dependencies"; helm_deps
for s in "${SERVICES[@]}"; do
  log "lint $s"; helm lint "$(chart_dir "$s")" --set signatures.activeSealKeyId=k --set database.password=x
done
log "lint part11-platform (development values)"
helm lint "$UMBRELLA_CHART" -f "$UMBRELLA_CHART/values-dev.yaml"
log "lint part11-platform (production values, secrets by reference)"
helm lint "$UMBRELLA_CHART"

log "render the platform with development values"
rendered="$(mktemp)"; trap 'rm -f "$rendered"' EXIT
helm template part11 "$UMBRELLA_CHART" -n part11 -f "$UMBRELLA_CHART/values-dev.yaml" > "$rendered"
grep -c '^kind:' "$rendered" | sed 's/^/rendered objects: /'
if command -v kubeconform >/dev/null 2>&1; then kubeconform -strict -summary "$rendered"; else warn "kubeconform not installed: skipped schema validation"; fi
log "ok"
