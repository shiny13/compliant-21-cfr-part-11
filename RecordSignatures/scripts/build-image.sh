#!/usr/bin/env bash
# Build the record-signatures container image (build context: the repository root).
#   RecordSignatures/scripts/build-image.sh [--tag TAG] [--registry REGISTRY] [--push]
exec "$(dirname "$0")/../../scripts/build-images.sh" "$@" record-signatures
