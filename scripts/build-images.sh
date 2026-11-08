#!/usr/bin/env bash
# Build the container images. The build context is the repository root (services share AuthenticationCore.Shared).
#
#   scripts/build-images.sh                              # all three, tag "local"
#   scripts/build-images.sh --tag 1.4.0 --registry registry.example.com/team --push
#   scripts/build-images.sh record-signatures            # one service
. "$(dirname "$0")/lib.sh"
need docker

TAG=local; REGISTRY=""; PUSH=0; WANT=()
while [ $# -gt 0 ]; do
  case "$1" in
    --tag) TAG="$2"; shift 2 ;;
    --registry) REGISTRY="$2"; shift 2 ;;
    --push) PUSH=1; shift ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    -*) die "unknown option $1" ;;
    *) WANT+=("$1"); shift ;;
  esac
done
[ ${#WANT[@]} -gt 0 ] || WANT=("${SERVICES[@]}")

REVISION="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
for svc in "${WANT[@]}"; do
  dir="${SERVICE_DIR[$svc]:-}"; [ -n "$dir" ] || die "unknown service '$svc' (expected one of: ${SERVICES[*]})"
  ref="$(image_ref "$svc" "$TAG" "$REGISTRY")"
  log "building $ref"
  docker build -f "$REPO_ROOT/$dir/Dockerfile" -t "$ref" \
    --label "org.opencontainers.image.title=$svc" \
    --label "org.opencontainers.image.revision=$REVISION" \
    --label "org.opencontainers.image.source=https://github.com/shiny13/compliant-21-cfr-part-11" \
    "$REPO_ROOT"
  if [ "$PUSH" = 1 ]; then log "pushing $ref"; docker push "$ref"; fi
done
log "done"
