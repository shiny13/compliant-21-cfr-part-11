#!/usr/bin/env bash
# Install or upgrade the whole platform on the current kubectl context with Helm.
#
#   scripts/deploy.sh --dev                              # laptop cluster, inline development secrets
#   scripts/generate-secrets.sh -n part11 && scripts/deploy.sh -n part11 --tag 1.4.0 --registry registry.example.com/team
#   scripts/deploy.sh -n part11 -f my-overrides.yaml --dry-run
#
# Options: -n NAMESPACE (part11)  -r RELEASE (part11)  --tag TAG  --registry REGISTRY  -f VALUES (repeatable)
#          --dev  --dry-run  --no-wait  --timeout 10m
. "$(dirname "$0")/lib.sh"
need helm

NS=part11; REL=part11; TAG=""; REGISTRY=""; DEV=0; DRY=0; WAIT=1; TIMEOUT=10m; VALUES=()
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --registry) REGISTRY="$2"; shift 2 ;;
    -f|--values) VALUES+=(-f "$2"); shift 2 ;;
    --dev) DEV=1; shift ;;
    --dry-run) DRY=1; shift ;;
    --no-wait) WAIT=0; shift ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done

log "resolving chart dependencies"; helm_deps

args=(upgrade --install "$REL" "$UMBRELLA_CHART" -n "$NS" --create-namespace -f "$UMBRELLA_CHART/values.yaml")
if [ "$DEV" = 1 ]; then
  warn "development values: public placeholder secrets. Never use against real data."
  args+=(-f "$UMBRELLA_CHART/values-dev.yaml")
else
  # Point each service at the Secrets scripts/generate-secrets.sh creates for this release name.
  args+=(--set "postgres.existingSecret=$REL-postgres"
         --set "authentication-core.signingKey.existingSecret=$REL-auth-signing-key" --set "authentication-core.secret.existingSecret=$REL-auth"
         --set "record-audit-trails.secret.existingSecret=$REL-audit" --set "record-signatures.secret.existingSecret=$REL-signatures")
fi
args+=("${VALUES[@]}")
if [ -n "$TAG" ]; then
  for s in "${SERVICES[@]}"; do args+=(--set "$s.image.tag=$TAG"); done
fi
if [ -n "$REGISTRY" ]; then
  for s in "${SERVICES[@]}"; do args+=(--set "$s.image.repository=${REGISTRY%/}/part11/$s"); done
fi
[ "$WAIT" = 1 ] && args+=(--wait --timeout "$TIMEOUT")
[ "$DRY" = 1 ] && args+=(--dry-run)

log "helm ${args[*]}"
helm "${args[@]}"
[ "$DRY" = 1 ] || log "deployed. Next: scripts/port-forward.sh -n $NS -r $REL, then scripts/smoke-test.sh"
