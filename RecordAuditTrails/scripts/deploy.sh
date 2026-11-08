#!/usr/bin/env bash
# Install or upgrade ONLY record-audit-trails (its own chart) from a values file of yours.
# For the whole platform use scripts/deploy.sh at the repository root.
#   RecordAuditTrails/scripts/deploy.sh -n NAMESPACE -r RELEASE -f my-values.yaml [--tag TAG] [--dry-run]
# Without the umbrella chart nothing is wired for you: set the base URLs of the other services in your values file
# (authentication.baseUrl, database.host) and the Secrets they need.
. "$(dirname "$0")/../../scripts/lib.sh"
need helm

NS=part11; REL=record-audit-trails; TAG=""; DRY=0; VALUES=()
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    -f|--values) VALUES+=(-f "$2"); shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --dry-run) DRY=1; shift ;;
    -h|--help) sed -n '2,7p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done
helm dependency update "$(chart_dir record-audit-trails)" >/dev/null
args=(upgrade --install "$REL" "$(chart_dir record-audit-trails)" -n "$NS" --create-namespace "${VALUES[@]}" --wait)
[ -n "$TAG" ] && args+=(--set "image.tag=$TAG")
[ "$DRY" = 1 ] && args+=(--dry-run)
helm "${args[@]}"
