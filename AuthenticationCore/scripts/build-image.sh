#!/usr/bin/env bash
# Build the authentication-core container image (build context: the repository root).
#   AuthenticationCore/scripts/build-image.sh [--tag TAG] [--registry REGISTRY] [--push]
exec "$(dirname "$0")/../../scripts/build-images.sh" "$@" authentication-core
