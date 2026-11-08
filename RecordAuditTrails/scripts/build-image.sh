#!/usr/bin/env bash
# Build the record-audit-trails container image (build context: the repository root).
#   RecordAuditTrails/scripts/build-image.sh [--tag TAG] [--registry REGISTRY] [--push]
exec "$(dirname "$0")/../../scripts/build-images.sh" "$@" record-audit-trails
