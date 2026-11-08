#!/usr/bin/env bash
# Shared helpers for the scripts in this repository. Source it: . "$(dirname "$0")/lib.sh"
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export REPO_ROOT

# service key -> "directory image-name chart-directory"
declare -A SERVICE_DIR=(
  [authentication-core]="AuthenticationCore"
  [record-audit-trails]="RecordAuditTrails"
  [record-signatures]="RecordSignatures"
)
SERVICES=(authentication-core record-audit-trails record-signatures)

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarn:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but was not found on PATH"; }

# Chart directory of one service.
chart_dir() {
  case "$1" in
    authentication-core) echo "$REPO_ROOT/AuthenticationCore/helm/authentication-core" ;;
    record-audit-trails) echo "$REPO_ROOT/RecordAuditTrails/helm/record-audit-trails" ;;
    record-signatures)   echo "$REPO_ROOT/RecordSignatures/helm/record-signatures" ;;
    *) die "unknown service '$1' (expected one of: ${SERVICES[*]})" ;;
  esac
}

# Random URL-safe secret of N bytes of entropy (default 32).
random_secret() { head -c "${1:-32}" /dev/urandom | base64 | tr -d '\n=' | tr '+/' '-_'; }

new_uuid() { cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen; }

# Resolve chart dependencies bottom-up: part11-common -> each service chart -> the umbrella chart.
helm_deps() {
  need helm
  local s
  for s in "${SERVICES[@]}"; do helm dependency update "$(chart_dir "$s")" >/dev/null; done
  helm dependency update "$REPO_ROOT/deploy/helm/part11-platform" >/dev/null
}

UMBRELLA_CHART="$REPO_ROOT/deploy/helm/part11-platform"

# Image reference for a service: [registry/]part11/<service>:<tag>
image_ref() { # service tag [registry]
  printf '%spart11/%s:%s' "${3:+${3%/}/}" "$1" "$2"
}
